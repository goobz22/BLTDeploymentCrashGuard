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
            // Fix the co-op client action-cache false-negative BEFORE their bootstrap
            // runs its verify — must beat their first (and only) attempt.
            ClientBootstrapFix.Apply(_harmony);
            // If the PREVIOUS session's co-op bootstrap aborted on a stale cache,
            // clear it before this session's bootstrap audits it.
            BootstrapWatch.CheckAtStartup();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            ApplyPatches();
            ClientBootstrapFix.Apply(_harmony); // retry in case the co-op assembly loaded late
            // All modules have loaded and applied their patches by now.
            TimeEnforcementGuard.Apply(_harmony);
            BattleMode.DecideAndApply(_harmony, "module-screen");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            TimeEnforcementGuard.Apply(_harmony); // retry in case the co-op assembly loaded late
            EncounterLoopGuard.Apply(_harmony);
            BattleMode.DecideAndApply(_harmony, "game-start");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            // Per-mission re-check: a friend joining/leaving flips the mode here too.
            BattleMode.DecideAndApply(_harmony, "mission-init");
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            Log.RefreshRole();          // self-throttled to once per 5s
            PlayerIdentityGuard.Tick(); // self-throttled to one check per second
            ShareTimeControl.Tick();    // self-throttled to once per 3s; host-only
            RoleTrace.Tick();           // self-throttled to once per second; logs on change
            LogStreamer.Tick();         // self-throttled to one upload per minute
            BootstrapWatch.Tick();      // self-throttled to one scan per 2 minutes
            ReportGuardActivity();      // self-throttled; logs which guards fired
        }

        private static int _lastActivityTick;
        private static string _lastActivity = "";

        private static void ReportGuardActivity()
        {
            try
            {
                int now = Environment.TickCount;
                if (_lastActivityTick != 0 && now - _lastActivityTick < 120000 && now >= _lastActivityTick)
                {
                    return;
                }
                _lastActivityTick = now;
                string summary = SelfHealing.FireSummary();
                if (summary != _lastActivity)
                {
                    _lastActivity = summary;
                    Log.Info(summary);
                }
            }
            catch
            {
            }
        }

        private static void ApplyPatches()
        {
            if (_patched)
            {
                return;
            }
            try
            {
                Log.Info(Diag.Banner());
                if (GuardConfig.Bool("safeMode", false))
                {
                    _patched = true;
                    Log.Info("SAFE MODE — all guards/fixes/tracers DISABLED via guardconfig.json safeMode=true. Set it to false and restart to re-enable.");
                    Log.Screen("SAFE MODE active — this mod is doing nothing (guardconfig.json)");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(SubModule).Assembly);

                // Always-on guards and fixes.
                TimeFlowPatch.Apply(_harmony);
                PartyAiCrashGuard.Apply(_harmony);
                EncounterLoopGuard.Apply(_harmony);
                MapClickSpeedKeeper.Apply(_harmony);
                ClientHeroCreationGuard.Apply(_harmony);
                ClanScreenCrashGuard.Apply(_harmony);

                // Verbose tracers — off unless troubleshooting (guardconfig tracing=true).
                if (GuardConfig.Bool("tracing", false))
                {
                    TracePatches.Apply(_harmony);
                    ControlTrace.Apply(_harmony);
                    TimeTrace.Apply(_harmony);
                    CoopBattleTrace.Apply(_harmony);
                    RoleTrace.Apply(_harmony);
                    Log.Info("tracing ENABLED (guardconfig tracing=true) — verbose diagnostic logging is on");
                }
                _patched = true;

                Log.Info("patches applied; battleMode=" + BattleMode.ConfigMode + " tracing=" + GuardConfig.Bool("tracing", false).ToString().ToLowerInvariant());
                Log.Info(Diag.HealthSummary());
                if (GuardConfig.Bool("selfTest", false))
                {
                    SelfHealing.RunSelfTests();
                }
            }
            catch (Exception ex)
            {
                Log.Info("FAILED to apply patches: " + ex);
            }
        }
    }

    /// <summary>
    /// Version/build identity, per-launch session id, and a startup HEALTH SUMMARY.
    /// Because every hook is by-name reflection, a BannerlordTogether update can
    /// silently break our patches; each module reports resolved/missing to Diag, and
    /// the summary surfaces "N/M active, MISSING: ..." on launch (and on screen if a
    /// critical fix failed to resolve), so version drift is never silent.
    /// </summary>
    internal static class Diag
    {
        internal const string Version = "1.1.0";

        internal static readonly string SessionId = GenerateSessionId();

        private static readonly System.Collections.Generic.List<string> _healthy = new System.Collections.Generic.List<string>();
        private static readonly System.Collections.Generic.List<string> _degraded = new System.Collections.Generic.List<string>();
        private static bool _criticalMissing;

        private static string GenerateSessionId()
        {
            // No Guid dependency needed; TickCount + pid is unique enough per launch.
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            return (Environment.TickCount ^ (pid << 8)).ToString("x8");
        }

        internal static string BuildTime()
        {
            try
            {
                return File.GetLastWriteTime(typeof(Diag).Assembly.Location).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unknown";
            }
        }

        internal static string Banner()
        {
            return "===== BLT Deployment Crash Guard v" + Version + " (build " + BuildTime() + ") session=" + SessionId + " =====";
        }

        /// <summary>A module reports its hook resolution. critical=true means the fix is
        /// load-bearing (e.g. the client bootstrap fix) and its absence is shown on screen.</summary>
        internal static void Report(string component, bool ok, string detail, bool critical = false)
        {
            if (ok)
            {
                _healthy.Add(component);
            }
            else
            {
                _degraded.Add(component + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                if (critical)
                {
                    _criticalMissing = true;
                }
            }
        }

        internal static string HealthSummary()
        {
            string summary = "MOD HEALTH: " + _healthy.Count + " active";
            if (_degraded.Count > 0)
            {
                summary += ", " + _degraded.Count + " NOT resolved -> " + string.Join("; ", _degraded.ToArray()) +
                           "  (likely a BannerlordTogether update renamed a method — check for a mod update)";
                if (_criticalMissing)
                {
                    Log.Screen("WARNING: a core BLT-guard fix did not load (BT may have updated) — see CrashGuard.log");
                }
            }
            else
            {
                summary += ", all resolved";
            }
            return summary;
        }
    }

    internal static class Log
    {
        private const long MaxLogBytes = 8 * 1024 * 1024; // roll over past 8 MB

        private static readonly object Sync = new object();
        private static string _path;
        private static string _roleTag = "?";
        private static int _lastRoleTick;
        private static bool _rotateChecked;

        internal static string CurrentPath
        {
            get { return LogPath; }
        }

        internal static string RoleTag
        {
            get { return _roleTag; }
        }

        /// <summary>H = hosting with peers, C = client, S = solo. Stamped on every
        /// line so two machines' logs can be merged side by side by timestamp.</summary>
        internal static void RefreshRole()
        {
            try
            {
                int now = Environment.TickCount;
                if (_lastRoleTick != 0 && now - _lastRoleTick < 5000 && now >= _lastRoleTick)
                {
                    return;
                }
                _lastRoleTick = now;
                if (PeerDetection.IsClient() == true)
                {
                    _roleTag = "C";
                }
                else if (PeerDetection.AnyRemotePeerConnected() == true)
                {
                    _roleTag = "H";
                }
                else
                {
                    _roleTag = "S";
                }
            }
            catch
            {
            }
        }

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
                    RotateIfNeeded();
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + _roleTag + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never take the game down
            }
        }

        /// <summary>Once per launch, if the log is already large, roll it to
        /// CrashGuard.log.1 so it never balloons unbounded (it hit 12 MB in a long
        /// session, which broke streaming). One backup is kept.</summary>
        private static void RotateIfNeeded()
        {
            if (_rotateChecked)
            {
                return;
            }
            _rotateChecked = true;
            try
            {
                string path = LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    string backup = path + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                    File.Move(path, backup);
                }
            }
            catch
            {
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
            SelfHealing.RecordFire("setup-teams-guard");
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
