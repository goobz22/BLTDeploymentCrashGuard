using System;
using System.IO;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Companion crash guard for BannerlordTogether siege/battle deployment.
    ///
    /// Root cause it protects against: the native DeploymentMissionController runs
    /// SetupTeams() on the first mission tick where Mission.Scene != null, and that
    /// method dereferences Mission.InitialPlayerAgent without a null check:
    ///
    ///     Agent initialPlayerAgent = base.Mission.InitialPlayerAgent;
    ///     initialPlayerAgent.Controller = AgentControllerType.None;   // NRE
    ///
    /// Mission._initialPlayerAgent is only assigned when an agent is built with
    /// Controller == Player. BannerlordTogether defers/replicates player-side spawns
    /// over the network in its SP-native co-op battles, so on sieges the player agent
    /// often does not exist yet when the scene finishes loading -> guaranteed crash.
    ///
    /// Fix strategy, three layers:
    ///  1. Hold the deployment tick (skip OnMissionTick) until InitialPlayerAgent
    ///     exists, so native team setup runs against a valid state. Capped so a
    ///     mission that never gets a player agent (e.g. spectator) cannot softlock.
    ///  2. Finalizer on SetupTeams: suppress any escaping exception (a throw here is
    ///     always an instant crash-to-desktop).
    ///  3. Finalizer on FinishDeployment: suppress + best-effort completion of the
    ///     method's tail steps so the battle stays playable instead of freezing.
    /// </summary>
    public class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "bltogether.deployment.crashguard";
        private static bool _patched;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            ApplyPatches();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            ApplyPatches();
        }

        private static void ApplyPatches()
        {
            if (_patched)
            {
                return;
            }
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(SubModule).Assembly);
                TracePatches.Apply(harmony);
                _patched = true;
                Log.Info("patches applied (deployment tick hold + SetupTeams/FinishDeployment crash guards + trace)");
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
    /// Layer 1: while team setup has not happened yet and the scene is ready, skip the
    /// deployment controller's tick until the player agent exists. Native SetupTeams()
    /// then runs against a fully valid mission state — this is the actual fix; the
    /// finalizers below are backstops.
    /// Skipping the original does not skip BannerlordTogether's own postfix on this
    /// method (Harmony postfixes still run), so its ready-gate/drain logic keeps working.
    /// </summary>
    [HarmonyPatch(typeof(DeploymentMissionController), "OnMissionTick")]
    internal static class DeploymentTickHoldPatch
    {
        private const float MaxHoldSeconds = 90f;

        private static DeploymentMissionController _tracked;
        private static float _heldSeconds;
        private static bool _holding;
        private static bool _capReleased;

        private static bool Prefix(DeploymentMissionController __instance, float dt)
        {
            try
            {
                if (!ReferenceEquals(_tracked, __instance))
                {
                    _tracked = __instance;
                    _heldSeconds = 0f;
                    _holding = false;
                    _capReleased = false;
                }

                if (__instance.TeamSetupOver)
                {
                    // done with this controller — drop the static reference so the
                    // finished mission isn't kept reachable between battles
                    _tracked = null;
                    _holding = false;
                    _heldSeconds = 0f;
                    _capReleased = false;
                    return true;
                }

                if (_capReleased)
                {
                    return true; // cap already fired for this controller — never re-hold or re-log
                }

                Mission mission = __instance.Mission;
                if (mission == null || mission.Scene == null)
                {
                    return true; // native tick no-ops in this state anyway
                }

                if (mission.InitialPlayerAgent != null)
                {
                    if (_holding)
                    {
                        Log.Info(string.Format("player agent arrived after holding {0:0.0}s — releasing native team setup", _heldSeconds));
                        _holding = false;
                    }
                    return true;
                }

                // Scene is ready but no player-controlled agent exists yet.
                _heldSeconds += dt;
                if (!_holding)
                {
                    _holding = true;
                    Log.Info("holding native deployment team setup: Mission.InitialPlayerAgent is null (waiting for player spawn/replication)");
                }
                if (_heldSeconds >= MaxHoldSeconds)
                {
                    Log.Info(string.Format("held {0:0.0}s without a player agent — releasing; crash-guard finalizers take over", _heldSeconds));
                    _holding = false;
                    _capReleased = true;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("tick-hold prefix error (passing through): " + ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Layer 2: an exception escaping SetupTeams() is an unconditional crash-to-desktop
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
    /// Layer 3: FinishDeployment dereferences Mission.InitialPlayerAgent too (and the
    /// field is re-nulled if the player agent is ever removed). On an escaping
    /// exception, run the method's remaining tail steps best-effort so the battle
    /// unfreezes (AI ticking back on, dying re-enabled, controller removed), then
    /// suppress.
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
