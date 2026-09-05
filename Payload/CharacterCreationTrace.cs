using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Two diagnostics, off unless guardconfig tracing=true; neither changes game behaviour.
    ///
    /// 1. CHARACTER-CREATION lifecycle ([CHARGEN]): logs the new-character flow
    ///    (OnInitialize / OnActivate / each stage / Refresh / Finalize) with the active
    ///    stage's type name, plus any exception a lifecycle method throws (finalizer —
    ///    observed, never swallowed). Added for the 2026-09-04 report of the banner-editor
    ///    preview rendering the character lying sideways.
    ///
    /// 2. SESSION-WIDE first-chance exception capture: armed once at Apply, it logs every
    ///    exception thrown in game code (SandBox / StoryMode / TaleWorlds, excluding
    ///    TaleWorlds.Library churn) with its FULL inner-exception chain and the throwing
    ///    frames — even when the game swallows it, and even when it is fatal. This exists
    ///    because the 2026-09-04 battle-load crash was a TypeInitializationException on
    ///    MovementOrder whose real cause lives in the INNER exception; ButterLib wrote no
    ///    report, so the mod must capture it itself. Coalesced by exception type + throwing
    ///    frame and capped, so a throw-in-a-loop cannot refill the log.
    /// </summary>
    internal static class CharacterCreationTrace
    {
        private const string StateType = "TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState";
        private const string ArmedSlot = "BLTCG_FirstChanceArmed"; // AppDomain slot: only ONE handler across payload generations
        private static int _firstChanceEmitted;
        private const int FirstChanceCap = 400;

        [ThreadStatic]
        private static bool _inHandler; // re-entrancy guard: never let the handler observe its own throws

        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            applied += Patch(harmony, StateType, "OnInitialize", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "OnActivate", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "OnStageActivated", nameof(StageActivatedPrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "Refresh", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "FinalizeCharacterCreationState", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            Arm();
            Log.Info("[CHARGEN] character-creation tracer active on " + applied + " method(s); session-wide first-chance exception capture " +
                     (IsArmed() ? "ARMED" : "NOT armed") + " (full inner-exception chains)");
        }

        private static int Patch(Harmony harmony, string typeName, string methodName, string prefixName, string finalizerName)
        {
            int count = 0;
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.Info("[CHARGEN] type not found: " + typeName);
                    return 0;
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(CharacterCreationTrace), prefixName) : null;
                        HarmonyMethod finalizer = finalizerName != null ? new HarmonyMethod(typeof(CharacterCreationTrace), finalizerName) : null;
                        harmony.Patch(method, prefix, null, null, finalizer);
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[CHARGEN] could not patch " + methodName + ": " + exOne.Message);
                    }
                }
                if (count == 0)
                {
                    Log.Info("[CHARGEN] no method named " + methodName + " on " + typeName);
                }
            }
            catch (Exception ex)
            {
                Log.Info("[CHARGEN] patch-by-name failed for " + methodName + ": " + ex.Message);
            }
            return count;
        }

        // ---- character-creation lifecycle hooks ----

        private static void LifecyclePrefix(MethodBase __originalMethod)
        {
            Log.Info("[CHARGEN] " + (__originalMethod != null ? __originalMethod.Name : "?"));
        }

        private static void StageActivatedPrefix(object[] __args)
        {
            string stage = "?";
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] != null)
                {
                    stage = __args[0].GetType().Name;
                }
            }
            catch
            {
            }
            Log.Info("[CHARGEN] stage -> " + stage);
            RuntimeDiagnostics.Mark("chargen-stage:" + stage); // memory + native-scene state per stage (folded-model hunt)
        }

        private static Exception LifecycleFinalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null)
            {
                Log.Info("[CHARGEN] EXCEPTION in " + (__originalMethod != null ? __originalMethod.Name : "?") + ": " + FormatChain(__exception));
            }
            return __exception; // never swallow
        }

        // ---- session-wide first-chance capture ----

        private static bool IsArmed()
        {
            try { return AppDomain.CurrentDomain.GetData(ArmedSlot) != null; }
            catch { return false; }
        }

        private static void Arm()
        {
            try
            {
                // Only one handler across all payload generations (a hot-reload leaves the
                // previous generation's handler attached; the slot stops them piling up).
                if (AppDomain.CurrentDomain.GetData(ArmedSlot) != null)
                {
                    return;
                }
                AppDomain.CurrentDomain.SetData(ArmedSlot, "1");
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            }
            catch (Exception ex)
            {
                Log.Info("[CHARGEN] could not arm first-chance capture: " + ex.Message);
            }
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (_inHandler || e == null || e.Exception == null)
            {
                return;
            }
            _inHandler = true;
            try
            {
                Exception ex = e.Exception;
                string top = ex.GetType().FullName ?? "Exception";
                if (top.StartsWith("BLTDeploymentCrashGuard", StringComparison.Ordinal))
                {
                    return; // our own internal catches are not interesting
                }
                string frame = FirstGameFrame(ex);
                if (frame == null)
                {
                    return; // no game frame -> framework-internal noise, skip
                }
                if (_firstChanceEmitted >= FirstChanceCap)
                {
                    return;
                }
                _firstChanceEmitted++;
                string key = "CHARGEN-FC " + ex.GetType().Name + " @ " + frame;
                // Live stack = who is ACTUALLY executing right now (shows the trigger the
                // exception's own truncated stack hides); plus the engine-state + memory
                // snapshot at the instant of the throw, to test the null-at-transition /
                // memory-pressure class hypothesis.
                string message = "[CHARGEN] first-chance " + FormatChain(ex)
                    + "\n   CONTEXT: " + RuntimeDiagnostics.StateContext()
                    + "\n   " + RuntimeDiagnostics.MemoryLine()
                    + RuntimeDiagnostics.LiveGameStack(2);
                TraceThrottle.Emit(key, message);
            }
            catch
            {
                // a tracer must never take the game down
            }
            finally
            {
                _inHandler = false;
            }
        }

        /// <summary>Full exception chain: outer, then each InnerException, each with its own
        /// throwing frames. A TypeInitializationException's real cause is always its inner —
        /// this is what the 2026-09-04 crash logger was missing.</summary>
        private static string FormatChain(Exception ex)
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (Exception cur = ex; cur != null && depth < 8; cur = cur.InnerException, depth++)
            {
                if (depth > 0)
                {
                    sb.Append("\n   <- INNER: ");
                }
                sb.Append(cur.GetType().FullName).Append(": ").Append(cur.Message);
                sb.Append(TrimStack(cur.StackTrace));
            }
            return sb.ToString();
        }

        /// <summary>The first stack frame that is game code (SandBox/StoryMode/TaleWorlds,
        /// excluding TaleWorlds.Library), used as the dedup key and the "who threw it" hint.</summary>
        private static string FirstGameFrame(Exception ex)
        {
            try
            {
                var trace = new StackTrace(ex, false);
                foreach (StackFrame f in trace.GetFrames() ?? new StackFrame[0])
                {
                    MethodBase m = f.GetMethod();
                    Type t = m != null ? m.DeclaringType : null;
                    string tn = t != null ? t.FullName : null;
                    if (tn == null)
                    {
                        continue;
                    }
                    if (tn.StartsWith("SandBox", StringComparison.Ordinal) ||
                        tn.StartsWith("StoryMode", StringComparison.Ordinal) ||
                        (tn.StartsWith("TaleWorlds", StringComparison.Ordinal) &&
                         !tn.StartsWith("TaleWorlds.Library", StringComparison.Ordinal)))
                    {
                        return tn + "." + m.Name;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string TrimStack(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }
            var sb = new StringBuilder();
            int shown = 0;
            foreach (string line in raw.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length == 0 ||
                    s.IndexOf("HarmonyLib", StringComparison.Ordinal) >= 0 ||
                    s.IndexOf("BLTDeploymentCrashGuard", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }
                sb.Append("\n      ").Append(s);
                if (++shown >= 14)
                {
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
