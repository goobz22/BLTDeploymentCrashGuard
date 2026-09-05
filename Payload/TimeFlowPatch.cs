using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// timeAlwaysFlows (guardconfig.json, default true): never auto-hold campaign time
    /// when the main party goes idle.
    ///
    /// Vanilla mechanism (verified in Campaign.TickMapTime): every tick sets
    /// IsMainPartyWaiting = MobileParty.MainParty.ComputeIsWaiting(), and the
    /// Stoppable play/fast-forward modes advance time only while that is false — so
    /// arriving at a clicked destination silently halts time without changing mode.
    /// This postfix forces ComputeIsWaiting to false for the MAIN party only, so time
    /// keeps flowing at the chosen speed. Real pauses (Stop mode via the pause button,
    /// menus, encounters) are untouched, as are AI parties and the wait-menu mode
    /// (UnstoppableFastForwardForPartyWaitTime), which never consults this flag.
    ///
    /// Health component `time-flow` (config-off is reported healthy as "disabled by config");
    /// fire id the same, counted once per idle arrival that was overridden; self-test
    /// `time-flow.contract` pins MobileParty.ComputeIsWaiting and the decision table
    /// (added 2026-09-04 — this fix used to be invisible to MOD HEALTH).
    /// </summary>
    internal static class TimeFlowPatch
    {
        private const string Component = "time-flow";
        private const string Tag = "[TIME-FLOW]";
        private const string TargetMethod = "ComputeIsWaiting";
        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static bool? _enabled;
        private static bool _loggedActive;
        private static bool _suppressing; // true while an idle-hold is being overridden — one fire per idle arrival, not per tick
        private static bool _applied;
        private static bool _testRegistered;

        private static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    _enabled = GuardConfig.Bool("timeAlwaysFlows", true);
                }
                return _enabled.Value;
            }
        }

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                int count = 0;
                foreach (MethodInfo method in Targets())
                {
                    harmony.Patch(method, null, new HarmonyMethod(typeof(TimeFlowPatch), nameof(Postfix)));
                    count++;
                }
                if (count == 0)
                {
                    Log.Info(Tag + " MobileParty." + TargetMethod + " not found — idle-hold suppressor inactive (game update?)");
                    Diag.Report(Component, false, "MobileParty." + TargetMethod + " not found (game update?)");
                    return;
                }
                _applied = true;
                Diag.Report(Component, true, Enabled ? "" : "disabled by config");
                Log.Info(Tag + " timeAlwaysFlows=" + Enabled.ToString().ToLowerInvariant() + " (patched " + count + " method(s))");
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        private static List<MethodInfo> Targets()
        {
            List<MethodInfo> found = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(MobileParty).GetMethods(AllDeclared))
            {
                if (method.Name == TargetMethod && !method.IsAbstract)
                {
                    found.Add(method);
                }
            }
            return found;
        }

        /// <summary>The whole decision, engine-free so the self-test can pin it: override the
        /// idle-hold only when the game computed "waiting", the feature is on, and this is the
        /// main party.</summary>
        internal static bool ShouldSuppressHold(bool computedWaiting, bool enabled, bool isMainParty)
        {
            return computedWaiting && enabled && isMainParty;
        }

        private static void Postfix(MobileParty __instance, ref bool __result)
        {
            try
            {
                bool isMain = __instance != null && __instance.IsMainParty;
                if (!ShouldSuppressHold(__result, Enabled, isMain))
                {
                    if (isMain && !__result)
                    {
                        _suppressing = false; // the party is moving again; the next idle arrival counts as a new fire
                    }
                    return;
                }
                __result = false;
                if (!_suppressing)
                {
                    _suppressing = true;
                    SelfHealing.RecordFire(Component);
                }
                if (!_loggedActive)
                {
                    _loggedActive = true;
                    Log.Info(Tag + " suppressing main-party idle-hold — time keeps flowing at the chosen speed (guardconfig timeAlwaysFlows=false to revert)");
                }
            }
            catch
            {
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool target = Targets().Count > 0;
            bool decisions =
                ShouldSuppressHold(true, true, true) &&
                !ShouldSuppressHold(false, true, true) &&   // not idle: nothing to override
                !ShouldSuppressHold(true, false, true) &&   // config off
                !ShouldSuppressHold(true, true, false);     // AI parties keep vanilla waiting
            bool pass = target && decisions;
            return SelfHealing.TestResult.Of(Component + ".contract", pass,
                pass ? "MobileParty." + TargetMethod + " and the idle-hold decision table verified"
                     : "target=" + target + " decisions=" + decisions);
        }
    }
}
