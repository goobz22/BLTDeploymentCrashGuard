using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only tracer for the NEW-CHARACTER / BANNER-EDITOR flow at co-op campaign setup.
    /// Symptom under investigation (field report 2026-09-04, screenshot): in a co-op setup
    /// the character in the banner-editor preview is rendered LYING SIDEWAYS instead of
    /// standing — a visuals/pose failure, the kind the engine typically swallows.
    ///
    /// This tracer does two things, only while character creation is active:
    ///  1. logs the character-creation lifecycle (OnInitialize / OnActivate / stage changes /
    ///     Refresh / Finalize) with the active stage's type name, and logs any exception a
    ///     lifecycle method throws (via a finalizer — observed, never swallowed);
    ///  2. arms an AppDomain FirstChanceException observer for the duration of the flow, so a
    ///     SWALLOWED exception in the scene/agent-visuals/pose path is named with its type,
    ///     message and the game frames that threw it. FirstChance can be chatty, so lines are
    ///     coalesced by exception-type + throwing frame and capped per activation.
    ///
    /// It is a diagnostic: off unless guardconfig tracing=true. It changes NO game behavior.
    /// </summary>
    internal static class CharacterCreationTrace
    {
        private const string StateType = "TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState";
        private static bool _armed;
        private static int _firstChanceEmitted;
        private const int FirstChanceCap = 300; // per activation, so a throw-in-a-loop cannot refill the log
        private static EventHandler<FirstChanceExceptionEventArgs> _handler;

        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            applied += Patch(harmony, StateType, "OnInitialize", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "OnActivate", nameof(ActivatePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "OnStageActivated", nameof(StageActivatedPrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "Refresh", nameof(LifecyclePrefix), nameof(LifecycleFinalizer));
            applied += Patch(harmony, StateType, "FinalizeCharacterCreationState", nameof(FinalizePrefix), nameof(LifecycleFinalizer));
            Log.Info("[CHARGEN] character-creation tracer active on " + applied + " method(s); first-chance exception capture arms while creating a character");
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

        // ---- lifecycle hooks ----

        private static void LifecyclePrefix(MethodBase __originalMethod)
        {
            Log.Info("[CHARGEN] " + (__originalMethod != null ? __originalMethod.Name : "?"));
        }

        private static void ActivatePrefix(MethodBase __originalMethod)
        {
            Arm();
            Log.Info("[CHARGEN] " + (__originalMethod != null ? __originalMethod.Name : "OnActivate") + " — first-chance capture ARMED");
        }

        private static void FinalizePrefix(MethodBase __originalMethod)
        {
            Log.Info("[CHARGEN] " + (__originalMethod != null ? __originalMethod.Name : "Finalize") + " — first-chance capture disarming (emitted " + _firstChanceEmitted + " this run)");
            Disarm();
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
        }

        private static Exception LifecycleFinalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null)
            {
                Log.Info("[CHARGEN] EXCEPTION in " + (__originalMethod != null ? __originalMethod.Name : "?") +
                         ": " + __exception.GetType().Name + ": " + __exception.Message + TrimStack(__exception.StackTrace));
            }
            return __exception; // never swallow
        }

        // ---- first-chance exception capture (armed only during character creation) ----

        private static void Arm()
        {
            if (_armed)
            {
                return;
            }
            _armed = true;
            _firstChanceEmitted = 0;
            TraceThrottle.Reset();
            try
            {
                _handler = OnFirstChance;
                AppDomain.CurrentDomain.FirstChanceException += _handler;
            }
            catch (Exception ex)
            {
                Log.Info("[CHARGEN] could not arm first-chance capture: " + ex.Message);
                _armed = false;
            }
        }

        private static void Disarm()
        {
            _armed = false;
            try
            {
                if (_handler != null)
                {
                    AppDomain.CurrentDomain.FirstChanceException -= _handler;
                    _handler = null;
                }
            }
            catch
            {
            }
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (!_armed || e == null || e.Exception == null)
            {
                return;
            }
            try
            {
                Exception ex = e.Exception;
                string type = ex.GetType().FullName ?? "Exception";
                // Ignore our own guards internal catches and pure-framework churn; keep game code.
                if (type.StartsWith("BLTDeploymentCrashGuard", StringComparison.Ordinal))
                {
                    return;
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
                string message = "[CHARGEN] first-chance " + ex.GetType().Name + ": " + ex.Message + TrimStack(ex.StackTrace);
                TraceThrottle.Emit(key, message);
            }
            catch
            {
                // a tracer must never take the game down
            }
        }

        /// <summary>The first stack frame that is game code (SandBox/TaleWorlds/StoryMode),
        /// used both as the dedup key and as the "who threw it" hint; null if none.</summary>
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
                if (++shown >= 12)
                {
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
