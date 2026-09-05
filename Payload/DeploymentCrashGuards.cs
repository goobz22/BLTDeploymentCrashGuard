using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Health + self-test for the two attribute-applied deployment crash guards below. They are
    /// installed by harmony.PatchAll, which reports nothing, so this class verifies after PatchAll
    /// that both finalizers are actually on their targets and reports it (tag [DEPLOY-GUARD]).
    ///
    /// Limitation, stated plainly: these guards stop the crash-to-desktop; they do NOT restore
    /// the missing player-side troops. A solo-host battle where BannerlordTogether stripped the
    /// player side still opens with empty formations — BattleMode (auto battle mode) is what
    /// prevents that by lifting BT's battle patches at the chokepoints; these finalizers are the
    /// last line when it cannot.
    /// </summary>
    internal static class DeploymentCrashGuardHealth
    {
        internal const string Component = "deployment-guards";
        internal const string Tag = "[DEPLOY-GUARD]";
        private static bool _applied;

        internal static void Apply()
        {
            if (_applied)
            {
                return;
            }
            _applied = true;
            try
            {
                SelfHealing.RegisterTest(SelfTest);
                MethodInfo setupTeams = AccessTools.Method(typeof(DeploymentMissionController), "SetupTeams");
                MethodInfo finishDeployment = AccessTools.Method(typeof(DeploymentMissionController), "FinishDeployment");
                bool setupGuarded = HasOurFinalizer(setupTeams);
                bool finishGuarded = HasOurFinalizer(finishDeployment);
                bool ok = setupGuarded && finishGuarded;
                string detail = "SetupTeams=" + Describe(setupTeams, setupGuarded) + " FinishDeployment=" + Describe(finishDeployment, finishGuarded);
                Diag.Report(Component, ok, ok ? "" : detail, critical: true);
                Log.Info(Tag + " deployment crash guards " + (ok ? "active" : "DEGRADED") + " — " + detail +
                         " (they suppress the CTD; the player side is restored by auto battle mode, not here)");
            }
            catch (Exception ex)
            {
                Diag.Report(Component, false, ex.Message, critical: true);
                Log.Info(Tag + " health check failed: " + ex.Message);
            }
        }

        private static string Describe(MethodInfo method, bool guarded)
        {
            return method == null ? "missing" : guarded ? "guarded" : "unpatched";
        }

        private static bool HasOurFinalizer(MethodInfo method)
        {
            if (method == null)
            {
                return false;
            }
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null || info.Finalizers == null)
            {
                return false;
            }
            foreach (Patch patch in info.Finalizers)
            {
                if (patch != null && BattleMode.IsOwnOwner(patch.owner))
                {
                    return true;
                }
            }
            return false;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = AccessTools.Method(typeof(DeploymentMissionController), "SetupTeams") != null &&
                            AccessTools.Method(typeof(DeploymentMissionController), "FinishDeployment") != null;
            bool inert = SetupTeamsCrashGuardPatch.Finalizer(null) == null &&
                         FinishDeploymentCrashGuardPatch.Finalizer(null, null) == null;
            bool pass = resolved && inert;
            return SelfHealing.TestResult.Of("deployment-guards.contract", pass,
                pass ? "both targets re-resolved; finalizers inert on null exception"
                     : "resolved=" + resolved + " inert=" + inert);
        }
    }

    /// <summary>
    /// An exception escaping SetupTeams() is an unconditional crash-to-desktop (it unwinds
    /// through Mission.OnTick into the engine). Suppress and log instead. Applied via PatchAll
    /// from the payload assembly.
    /// </summary>
    [HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
    internal static class SetupTeamsCrashGuardPatch
    {
        internal static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("setup-teams-guard");
            Log.Info(DeploymentCrashGuardHealth.Tag + " SUPPRESSED crash in DeploymentMissionController.SetupTeams: " + __exception);
            Log.Screen("prevented a deployment-setup crash (details in Modules/BLTDeploymentCrashGuard/CrashGuard.log)");
            return null;
        }
    }

    /// <summary>
    /// FinishDeployment dereferences Mission.InitialPlayerAgent too (and the field is re-nulled
    /// if the player agent is ever removed). On an escaping exception, run the method's remaining
    /// tail steps best-effort so the battle unfreezes, then suppress.
    /// </summary>
    [HarmonyPatch(typeof(DeploymentMissionController), "FinishDeployment")]
    internal static class FinishDeploymentCrashGuardPatch
    {
        internal static Exception Finalizer(Exception __exception, DeploymentMissionController __instance)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("finish-deployment-guard");
            Log.Info(DeploymentCrashGuardHealth.Tag + " SUPPRESSED crash in DeploymentMissionController.FinishDeployment: " + __exception);
            try
            {
                Mission mission = __instance != null ? __instance.Mission : null;
                if (mission != null)
                {
                    Agent player = mission.InitialPlayerAgent;
                    if (player != null)
                    {
                        try
                        {
                            player.SetDetachableFromFormation(true);
                            player.Controller = AgentControllerType.Player;
                        }
                        catch (Exception exPlayer)
                        {
                            Log.Info(DeploymentCrashGuardHealth.Tag + " recovery (player agent handover): " + exPlayer);
                        }
                    }
                    // Each tail step in its own try/catch so one failing step cannot abort the rest
                    // (the rule docs/MODDING-GUIDE.md teaches from this very file).
                    try { mission.AllowAiTicking = true; }
                    catch (Exception exAi) { Log.Info(DeploymentCrashGuardHealth.Tag + " recovery AllowAiTicking: " + exAi.Message); }
                    try { mission.DisableDying = false; }
                    catch (Exception exDying) { Log.Info(DeploymentCrashGuardHealth.Tag + " recovery DisableDying: " + exDying.Message); }
                    try { mission.SetFallAvoidSystemActive(false); } catch { }
                    try { mission.OnAfterDeploymentFinished(); }
                    catch (Exception ex2) { Log.Info(DeploymentCrashGuardHealth.Tag + " recovery OnAfterDeploymentFinished: " + ex2); }
                    try { AccessTools.Method(__instance.GetType(), "AfterDeploymentFinished")?.Invoke(__instance, null); }
                    catch (Exception ex3) { Log.Info(DeploymentCrashGuardHealth.Tag + " recovery AfterDeploymentFinished: " + ex3); }
                    try { mission.RemoveMissionBehavior(__instance); }
                    catch (Exception ex4) { Log.Info(DeploymentCrashGuardHealth.Tag + " recovery RemoveMissionBehavior: " + ex4); }
                }
            }
            catch (Exception exRecovery)
            {
                Log.Info(DeploymentCrashGuardHealth.Tag + " FinishDeployment recovery failed: " + exRecovery);
            }
            Log.Screen("prevented a deployment-finish crash (details in Modules/BLTDeploymentCrashGuard/CrashGuard.log)");
            return null;
        }
    }
}
