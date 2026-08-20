using System;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// An exception escaping SetupTeams() is an unconditional crash-to-desktop (it unwinds
    /// through Mission.OnTick into the engine). Suppress and log instead. Applied via PatchAll
    /// from the payload assembly.
    /// </summary>
    [HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
    internal static class SetupTeamsCrashGuardPatch
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("setup-teams-guard");
            Log.Info("SUPPRESSED crash in DeploymentMissionController.SetupTeams: " + __exception);
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
        private static Exception Finalizer(Exception __exception, DeploymentMissionController __instance)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("finish-deployment-guard");
            Log.Info("SUPPRESSED crash in DeploymentMissionController.FinishDeployment: " + __exception);
            try
            {
                Mission mission = __instance.Mission;
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
                            Log.Info("recovery (player agent handover): " + exPlayer);
                        }
                    }
                    mission.AllowAiTicking = true;
                    mission.DisableDying = false;
                    try { mission.SetFallAvoidSystemActive(false); } catch { }
                    try { mission.OnAfterDeploymentFinished(); }
                    catch (Exception ex2) { Log.Info("recovery OnAfterDeploymentFinished: " + ex2); }
                    try { AccessTools.Method(__instance.GetType(), "AfterDeploymentFinished")?.Invoke(__instance, null); }
                    catch (Exception ex3) { Log.Info("recovery AfterDeploymentFinished: " + ex3); }
                    try { mission.RemoveMissionBehavior(__instance); }
                    catch (Exception ex4) { Log.Info("recovery RemoveMissionBehavior: " + ex4); }
                }
            }
            catch (Exception exRecovery)
            {
                Log.Info("FinishDeployment recovery failed: " + exRecovery);
            }
            Log.Screen("prevented a deployment-finish crash (details in Modules/BLTDeploymentCrashGuard/CrashGuard.log)");
            return null;
        }
    }
}
