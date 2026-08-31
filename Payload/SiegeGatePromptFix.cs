using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Restores the missing "F: Close / F: Open" prompt on siege gates (field report
    /// 2026-08-30: defending a castle, gate open, no close prompt). Root cause, proven from
    /// the installed build's IL: CastleGate.ServerTick activates the gate's standing points
    /// ONLY when the door skeleton's animation parameter is EXACTLY >= 1.0 —
    /// `if (animParam &lt; 1f) deactivate ALL points; else deactivate the points whose tag
    /// matches the wrong direction`. Two ways that exactness fails and every interaction
    /// point stays dead:
    ///  - vanilla's own initial state parks a closed gate at parameter 0.99 and FREEZES the
    ///    skeleton (SetInitialStateOfGate), so a never-cycled gate has no prompts at all;
    ///  - an opened gate whose animation settles a float-hair under 1.0 never re-activates
    ///    its close points.
    /// Fix: postfix on ServerTick — when the parameter is in [0.98, 1.0) (i.e. the door is
    /// visually at rest but vanilla's exact test failed), apply vanilla's OWN tag rule:
    /// gate Open -> "open"-tagged points off, close points on; gate Closed -> the reverse.
    /// Mid-swing doors (&lt; 0.98) keep vanilla's everything-off behavior. A DESTROYED gate
    /// (battering ram) is left exactly as vanilla wants it — broken gates cannot be closed —
    /// but with tracing on, the log says so explicitly instead of leaving a mystery.
    /// Works in vanilla and co-op (missions are local; BT has no gate code).
    /// </summary>
    internal static class SiegeGatePromptFix
    {
        /// <summary>The door is considered at rest from here up; vanilla demands exactly 1.0.</summary>
        private const float RestThreshold = 0.98f;

        private static FieldInfo _doorSkeletonField;
        private static int _lastFixLogTick;
        private static int _lastDestroyedLogTick;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _doorSkeletonField = AccessTools.Field(typeof(CastleGate), "_doorSkeleton");
                MethodInfo serverTick = AccessTools.Method(typeof(CastleGate), "ServerTick");
                if (_doorSkeletonField == null || serverTick == null)
                {
                    Log.Info("[GATE] siege gate-prompt fix inactive — members not resolved (game update?)");
                    Diag.Report("siege-gate-prompt-fix", false, "members not resolved");
                    return;
                }
                harmony.Patch(serverTick, null, new HarmonyMethod(typeof(SiegeGatePromptFix), nameof(ServerTickPostfix)));
                Log.Info("[GATE] siege gate-prompt fix active — gates at rest always offer their F interaction (vanilla requires an exact 1.0 animation parameter and parks gates at 0.99)");
                Diag.Report("siege-gate-prompt-fix", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[GATE] siege gate-prompt apply failed: " + ex.Message);
                Diag.Report("siege-gate-prompt-fix", false, ex.Message);
            }
        }

        private static void ServerTickPostfix(CastleGate __instance)
        {
            try
            {
                if (__instance.IsDeactivated)
                {
                    return; // machine-level deactivation is deliberate — respect it
                }
                if (__instance.IsDestroyed)
                {
                    // A ram-broken gate hangs open but is GONE for this battle by design.
                    int nowD = Environment.TickCount;
                    if (GuardConfig.Bool("tracing", false) &&
                        (_lastDestroyedLogTick == 0 || nowD - _lastDestroyedLogTick >= 30000 || nowD < _lastDestroyedLogTick))
                    {
                        _lastDestroyedLogTick = nowD;
                        Log.Info("[GATE] gate is DESTROYED — vanilla does not allow closing a broken gate (no prompt is correct here)");
                    }
                    return;
                }
                Skeleton skeleton = _doorSkeletonField.GetValue(__instance) as Skeleton;
                if (skeleton == null)
                {
                    return;
                }
                float parameter = skeleton.GetAnimationParameterAtChannel(0);
                if (parameter >= 1f || parameter < RestThreshold)
                {
                    return; // >= 1: vanilla activated correctly; < 0.98: genuinely mid-swing
                }
                // Door at rest but vanilla's exact-1.0 test left every point deactivated —
                // apply vanilla's own direction rule.
                string excludedTag = __instance.State == CastleGate.GateState.Closed ? "close" : "open";
                int activated = 0;
                foreach (StandingPoint point in __instance.StandingPoints)
                {
                    bool deactivate = point.GameEntity.HasTag(excludedTag);
                    if (point.IsDeactivated != deactivate)
                    {
                        point.SetIsDeactivatedSynched(deactivate);
                        if (!deactivate)
                        {
                            activated++;
                        }
                    }
                }
                if (activated > 0)
                {
                    SelfHealing.RecordFire("siege-gate-prompt-fix");
                    int now = Environment.TickCount;
                    if (_lastFixLogTick == 0 || now - _lastFixLogTick >= 5000 || now < _lastFixLogTick)
                    {
                        _lastFixLogTick = now;
                        Log.Info("[GATE] re-activated " + activated + " gate standing point(s) — door at rest (param " +
                                 parameter.ToString("0.###") + " < vanilla's exact 1.0 requirement), state " + __instance.State +
                                 " — the F " + (excludedTag == "open" ? "Close" : "Open") + " prompt is available again");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[GATE] prompt-fix tick error: " + ex.Message);
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = _doorSkeletonField != null &&
                            AccessTools.Method(typeof(CastleGate), "ServerTick") != null &&
                            AccessTools.Method(typeof(StandingPoint), "SetIsDeactivatedSynched") != null;
            // The decision band: exact-1.0 and mid-swing are vanilla's; only the frozen
            // at-rest gap [0.98, 1.0) is ours.
            bool band = Decide(1.0f) == false && Decide(0.99f) == true && Decide(0.5f) == false && Decide(0.981f) == true;
            bool pass = resolved && band;
            return SelfHealing.TestResult.Of("siege-gate-prompt-fix.contract", pass,
                pass ? "members re-resolved; correction band [0.98,1.0) verified"
                     : "resolved=" + resolved + " band=" + band);
        }

        /// <summary>True when the postfix would correct the standing points for this
        /// animation parameter (the testable core of the decision).</summary>
        internal static bool Decide(float parameter)
        {
            return parameter < 1f && parameter >= RestThreshold;
        }
    }
}
