using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Memory + engine-state telemetry, and the shared context/stack helpers the exception
    /// capture uses. Built 2026-09-04 to chase a suspected CLASS (not three separate bugs):
    /// an AccessViolationException in NativeObject.Finalize (native memory freed under us), a
    /// character rendered folded/sideways (native mesh/skeleton), and a MovementOrder NRE
    /// (managed engine state null at a mission transition). The unifying hypothesis is engine
    /// state / native memory being touched while half-initialized or already freed, possibly
    /// under memory or cache pressure.
    ///
    /// So we log, every ~15 s and at every mission/scene transition: process working set,
    /// private bytes and managed heap, GC collection counts, and the current game-state /
    /// mission / campaign snapshot — enough to see a leak or a balloon build up before a
    /// symptom, and to know the exact state the engine was in when an exception fired.
    ///
    /// Off unless guardconfig tracing=true; changes no game behaviour.
    /// </summary>
    internal static class RuntimeDiagnostics
    {
        private static int _lastHeartbeatTick;
        private static long _peakWorkingSet;
        private static long _peakManaged;
        private const int HeartbeatMs = 15000;

        /// <summary>Set true when tracing is on (PayloadEntry). Keeps the memory/state
        /// heartbeat out of a normal player's session while leaving it on for troubleshooting.</summary>
        internal static bool Enabled;

        internal static void Heartbeat()
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                int now = Environment.TickCount;
                if (_lastHeartbeatTick != 0 && now - _lastHeartbeatTick < HeartbeatMs && now >= _lastHeartbeatTick)
                {
                    return;
                }
                _lastHeartbeatTick = now;
                Log.Info("[DIAG] " + MemoryLine() + " | " + StateContext());
            }
            catch
            {
            }
        }

        /// <summary>Force a labelled memory+state line now (mission init, battle start, a stage
        /// change) regardless of the heartbeat interval.</summary>
        internal static void Mark(string label)
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                _lastHeartbeatTick = Environment.TickCount;
                Log.Info("[DIAG] " + label + " | " + MemoryLine() + " | " + StateContext());
            }
            catch
            {
            }
        }

        internal static string MemoryLine()
        {
            try
            {
                Process p = Process.GetCurrentProcess();
                long ws = p.WorkingSet64;
                long priv = p.PrivateMemorySize64;
                long managed = GC.GetTotalMemory(false);
                if (ws > _peakWorkingSet) _peakWorkingSet = ws;
                if (managed > _peakManaged) _peakManaged = managed;
                return "mem WS=" + Mb(ws) + " priv=" + Mb(priv) + " managed=" + Mb(managed) +
                       " peakWS=" + Mb(_peakWorkingSet) + " gc0/1/2=" +
                       GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) +
                       " handles=" + SafeHandles(p) + " threads=" + SafeThreads(p);
            }
            catch (Exception ex)
            {
                return "mem (unavailable: " + ex.GetType().Name + ")";
            }
        }

        /// <summary>Current engine state — the fields most relevant to the null-at-transition
        /// hypothesis: is there a Mission, what state is it in, is there a Campaign, what game
        /// state is active. Every access is guarded because during a transition any of these
        /// can itself throw.</summary>
        internal static string StateContext()
        {
            var sb = new StringBuilder();
            sb.Append("Mission=").Append(MissionDesc());
            sb.Append(" GameState=").Append(GameStateDesc());
            sb.Append(" Campaign=").Append(CampaignDesc());
            return sb.ToString();
        }

        private static string MissionDesc()
        {
            try
            {
                var m = TaleWorlds.MountAndBlade.Mission.Current;
                if (m == null)
                {
                    return "null";
                }
                string mode; try { mode = m.Mode.ToString(); } catch { mode = "?"; }
                string state; try { state = m.CurrentState.ToString(); } catch { state = "?"; }
                bool sceneNull; try { sceneNull = m.Scene == null; } catch { sceneNull = true; }
                return "live(mode=" + mode + ",state=" + state + (sceneNull ? ",scene=null" : "") + ")";
            }
            catch (Exception ex)
            {
                return "threw:" + ex.GetType().Name;
            }
        }

        private static string GameStateDesc()
        {
            try
            {
                var gsm = TaleWorlds.Core.GameStateManager.Current;
                if (gsm == null)
                {
                    return "null";
                }
                var active = gsm.ActiveState;
                return active != null ? active.GetType().Name : "none";
            }
            catch (Exception ex)
            {
                return "threw:" + ex.GetType().Name;
            }
        }

        private static string CampaignDesc()
        {
            try
            {
                var c = TaleWorlds.CampaignSystem.Campaign.Current;
                return c == null ? "null" : "set";
            }
            catch (Exception ex)
            {
                return "threw:" + ex.GetType().Name;
            }
        }

        /// <summary>The LIVE call stack of the current thread (game frames only), captured at
        /// the moment an exception fires — this is what shows WHO triggered a static-init or a
        /// null deref, which the exception's own truncated stack does not.</summary>
        internal static string LiveGameStack(int skip)
        {
            try
            {
                var trace = new StackTrace(skip, false);
                var sb = new StringBuilder();
                int shown = 0;
                foreach (StackFrame f in trace.GetFrames() ?? new StackFrame[0])
                {
                    MethodBase m = f.GetMethod();
                    Type t = m != null ? m.DeclaringType : null;
                    string tn = t != null ? t.FullName : null;
                    string name = tn != null ? tn + "." + m.Name : (m != null ? m.Name : null);
                    if (name == null)
                    {
                        continue;
                    }
                    if (tn != null && (tn.StartsWith("System.", StringComparison.Ordinal) ||
                                       tn.StartsWith("HarmonyLib", StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    sb.Append("\n      LIVE ").Append(name);
                    if (++shown >= 20)
                    {
                        break;
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string SafeHandles(Process p)
        {
            try { return p.HandleCount.ToString(); } catch { return "?"; }
        }

        private static string SafeThreads(Process p)
        {
            try { return p.Threads.Count.ToString(); } catch { return "?"; }
        }

        private static string Mb(long bytes)
        {
            return (bytes / (1024 * 1024)) + "MB";
        }
    }
}
