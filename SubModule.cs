using System;
using System.IO;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Companion guard for BannerlordTogether battles.
    ///
    /// Root problem (proven 2026-08-18, host-solo co-op): the mod's battle-mission
    /// pipeline leaves the player's side without any mission troops — the native
    /// DeploymentMissionController.SetupTeams() then dereferences the never-created
    /// Mission.InitialPlayerAgent and crashes to desktop:
    ///
    ///     Agent initialPlayerAgent = base.Mission.InitialPlayerAgent;
    ///     initialPlayerAgent.Controller = AgentControllerType.None;   // NRE
    ///
    /// Layers:
    ///  1. SoloVanillaBattles (default ON): remove foreign Harmony patches from the
    ///     battle/deployment/spawn methods so battles run pure vanilla while hosting
    ///     solo. See SoloVanillaBattles.cs.
    ///  2. Finalizer on SetupTeams: suppress any escaping exception (a throw here is
    ///     always an instant crash-to-desktop).
    ///  3. Finalizer on FinishDeployment: suppress + best-effort completion of the
    ///     method's tail steps so the battle stays playable instead of freezing.
    ///
    /// (v1 also had a "hold the tick until InitialPlayerAgent exists" prefix. Removed:
    /// vanilla creates the player agent INSIDE SetupTeams' own spawn step, so the hold
    /// could never succeed — it only delayed every deployment. Evidence: 2026-08-18
    /// 23:04:41, a 90s hold expired and SetupTeams still NRE'd.)
    /// </summary>
    public class SubModule : MBSubModuleBase
    {
        internal const string HarmonyId = "bltogether.deployment.crashguard";

        private static Harmony _harmony;
        private static bool _patched;

        internal static Harmony HarmonyInstance
        {
            get { return _harmony; }
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            ApplyPatches();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            ApplyPatches();
            // All modules have loaded and applied their patches by now.
            TimeEnforcementGuard.Apply(_harmony);
            BattleMode.DecideAndApply(_harmony, "module-screen");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            TimeEnforcementGuard.Apply(_harmony); // retry in case the co-op assembly loaded late
            BattleMode.DecideAndApply(_harmony, "game-start");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            // Per-mission re-check: a friend joining/leaving flips the mode here too.
            BattleMode.DecideAndApply(_harmony, "mission-init");
        }

        private static void ApplyPatches()
        {
            if (_patched)
            {
                return;
            }
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(SubModule).Assembly);
                TracePatches.Apply(_harmony);
                ControlTrace.Apply(_harmony);
                TimeTrace.Apply(_harmony);
                TimeFlowPatch.Apply(_harmony);
                _patched = true;
                Log.Info("patches applied (crash guards + trace + control trace + time trace); battleMode=" + BattleMode.ConfigMode);
            }
            catch (Exception ex)
            {
                Log.Info("FAILED to apply patches: " + ex);
            }
        }
    }

    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        private static string LogPath
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        string binDir = Path.GetDirectoryName(typeof(Log).Assembly.Location);
                        string moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                        _path = Path.Combine(moduleRoot, "CrashGuard.log");
                    }
                    catch
                    {
                        _path = "BLTDeploymentCrashGuard.log";
                    }
                }
                return _path;
            }
        }

        internal static void Info(string message)
        {
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never take the game down
            }
        }

        internal static void Screen(string message)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage("[Deploy Guard] " + message, new Color(1f, 0.75f, 0.3f)));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// An exception escaping SetupTeams() is an unconditional crash-to-desktop
    /// (it unwinds through Mission.OnTick into the engine). Suppress and log instead.
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
            Log.Info("SUPPRESSED crash in DeploymentMissionController.SetupTeams: " + __exception);
            Log.Screen("prevented a deployment-setup crash (details in Modules/BLTDeploymentCrashGuard/CrashGuard.log)");
            return null;
        }
    }

    /// <summary>
    /// FinishDeployment dereferences Mission.InitialPlayerAgent too (and the field is
    /// re-nulled if the player agent is ever removed). On an escaping exception, run
    /// the method's remaining tail steps best-effort so the battle unfreezes
    /// (AI ticking back on, dying re-enabled, controller removed), then suppress.
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
