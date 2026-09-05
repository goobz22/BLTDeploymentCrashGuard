using System;
using System.IO;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// The hot-reloadable payload's entry point. The harness instantiates this from a freshly
    /// loaded assembly each generation and calls Apply once, then forwards the game lifecycle.
    /// Everything here has fresh statics per generation (that is what makes reload clean).
    /// </summary>
    public sealed class PayloadEntry : IPayload
    {
        /// <summary>The current generation's Harmony instance — read by guards that (re)patch
        /// on the fly (TracePatches, BattleMode). Per-generation because statics are fresh.</summary>
        internal static Harmony Harmony;
        internal static ISharedState Shared;

        private static int _lastActivityTick;
        private static string _lastActivity = "";
        private static int _lastRoleTick;

        public void Apply(Harmony harmony, ISharedState shared)
        {
            Harmony = harmony;
            Shared = shared;
            try
            {
                Log.Info("payload " + PayloadBuild() + " applying on " + harmony.Id);

                if (GuardConfig.Bool("safeMode", false))
                {
                    Log.Info("SAFE MODE — all guards/fixes/tracers DISABLED via guardconfig.json safeMode=true.");
                    Log.Screen("SAFE MODE active — this mod is doing nothing (guardconfig.json)");
                    return;
                }

                // MUST be first: initialize the MovementOrder struct safely before any patch
                // that references Formation/OrderController makes the CLR prepare (and, because
                // it is beforefieldinit, run the static ctor of) MovementOrder while Mission.Current
                // is null — which permanently poisons the type and crashes every battle.
                MovementOrderTypeInitGuard.ApplyEarly(harmony);

                // Attribute-based patches (the two deployment crash finalizers) for THIS assembly.
                harmony.PatchAll(typeof(PayloadEntry).Assembly);
                DeploymentCrashGuardHealth.Apply(); // verifies + reports the two attribute-applied finalizers

                // Always-on guards and fixes.
                TimeFlowPatch.Apply(harmony);
                PartyAiCrashGuard.Apply(harmony);
                BattleMode.Apply(harmony); // always-on battle chokepoints (StartBattle/OpenNew) + health/self-test
                EncounterLoopGuard.Apply(harmony);
                MapClickSpeedKeeper.Apply(harmony);
                ClientHeroCreationGuard.Apply(harmony);
                ClanScreenCrashGuard.Apply(harmony);
                IllnessDeathGuard.Apply(harmony);
                ClanModeSoloFix.Apply(harmony);
                MarriageBarterGuard.Apply(harmony);
                ConversationCameraCrashGuard.Apply(harmony);
                DeadHeroReactivationFix.Apply(harmony);
                MapIncidentCrashGuard.Apply(harmony);
                BackgroundTickBudgetGuard.Apply(harmony);
                CivilianGateCloseFix.Apply(harmony);
                SiegeGatePromptFix.Apply(harmony);
                SiegeCommandGuard.Apply(harmony);
                CoopCommandSplit.Apply(harmony);
                CoopHeroIdentityLock.Apply();
                StealthHideoutAdvisor.Apply(harmony);
                ClanPartyCreationAdvisor.Apply(harmony);
                JoinSyncPauseEscape.Apply(harmony);
                PregnancySync.PregnancySyncGuard.Apply(harmony);
                StashSync.StashSyncGuard.Apply(harmony);

                // Client bootstrap fix — must beat BT's first (and only) verify on a fresh process;
                // on a mid-game reload it just installs the prefix (BT won't verify again).
                ClientBootstrapFix.Apply(harmony);
                TimeEnforcementGuard.Apply(harmony);

                // Verbose tracers — off unless troubleshooting. The tracing flag is read FRESH
                // from disk here (the harness's GuardConfig caches the file for the whole game
                // session) so that flipping guardconfig.json + a payload hot-reload turns the
                // tracers on mid-session without restarting the game and losing the live repro.
                bool tracing = FreshTracingFlag();
                if (tracing)
                {
                    TracePatches.Apply(harmony);
                    ControlTrace.Apply(harmony);
                    TimeTrace.Apply(harmony);
                    CoopBattleTrace.Apply(harmony);
                    CharacterCreationTrace.Apply(harmony);
                    MovementOrderInitProbe.Apply(harmony); // origin probe for the MovementOrder type-init crash
                    RoleTrace.Apply(harmony);
                    RuntimeDiagnostics.Enabled = true; // memory/state heartbeat + rich exception context
                    Log.Info("tracing ENABLED (guardconfig tracing=true)");
                }

                // Clear a stale co-op action cache from a previous aborted session.
                BootstrapWatch.CheckAtStartup();

                // Establish battle mode now (so a mid-game reload re-decides immediately).
                BattleMode.DecideAndApply(harmony, "apply");

                Log.Info("patches applied; battleMode=" + BattleMode.ConfigMode + " tracing=" + tracing.ToString().ToLowerInvariant());
                Log.Info(Diag.HealthSummary());
                if (GuardConfig.Bool("selfTest", false))
                {
                    SelfHealing.RunSelfTests();
                }
            }
            catch (Exception ex)
            {
                Log.Info("FAILED to apply patches: " + ex);
                throw; // let the harness keep the previous generation on a failed apply
            }
        }

        public void OnBeforeInitialModuleScreen()
        {
            ClientBootstrapFix.Apply(Harmony); // retry in case the co-op assembly loaded late
            ClanModeSoloFix.Apply(Harmony);    // same late-BT-assembly retry (latched once applied)
            JoinSyncPauseEscape.Apply(Harmony); // same late-BT-assembly retry (latched once applied)
            BackgroundTickBudgetGuard.Apply(Harmony); // same late-BT-assembly retry (latched once applied)
            SiegeCommandGuard.RetryBt(Harmony); // hook BT's host player-down releases if BT loaded after us
            TimeEnforcementGuard.Apply(Harmony);
            BattleMode.DecideAndApply(Harmony, "module-screen");
        }

        public void OnGameStart()
        {
            TimeEnforcementGuard.Apply(Harmony);
            EncounterLoopGuard.Apply(Harmony);
            BattleMode.DecideAndApply(Harmony, "game-start");
            PregnancySync.PregnancySyncGuard.OnGameStart(); // per-campaign host birth listener
            CoopHeroIdentityLock.OnGameStart(); // arm the this-machine's-hero claim for the loaded campaign
        }

        public void OnMissionInit()
        {
            BattleMode.DecideAndApply(Harmony, "mission-init");
            SiegeCommandGuard.OnMissionInit(); // per-battle counters and hand-off depth reset
            CoopCommandSplit.OnMissionInit(); // re-resolve the two players' parties per battle
            RuntimeDiagnostics.Mark("mission-init"); // memory + engine-state snapshot at every mission transition
        }

        public void Tick()
        {
            RefreshRole();
            PlayerIdentityGuard.Tick();
            ShareTimeControl.Tick();
            RoleTrace.Tick();
            LogStreamer.Tick();
            BootstrapWatch.Tick();
            PregnancySync.PregnancySyncGuard.Tick(); // drain queued client birth reconstructions
            StashSync.StashSyncGuard.Tick(); // drain queued peer stash updates
            CoopHeroIdentityLock.Tick(); // claim this machine's hero once the map is up
            ClanPartyCreationAdvisor.Tick(); // open the troop exchange for a just-created party
            CoopCommandSplit.Tick(); // keep each co-op player's troops in their own formation block
            RuntimeDiagnostics.Heartbeat(); // periodic memory + engine-state telemetry (tracing only)
            ReportGuardActivity();
        }

        /// <summary>Compute the H/C/S role tag and hand it to the harness logger.</summary>
        private static void RefreshRole()
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
                    Log.SetRoleTag("C");
                }
                else if (PeerDetection.AnyRemotePeerConnected() == true)
                {
                    Log.SetRoleTag("H");
                }
                else
                {
                    Log.SetRoleTag("S");
                }
            }
            catch
            {
            }
        }

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

        /// <summary>Read guardconfig.json's tracing flag straight from disk. The harness's
        /// GuardConfig caches the file text for the whole game session, which made tracing
        /// impossible to enable via hot-reload mid-session; a fresh read fixes that. Falls
        /// back to the cached value if the file is unreadable.</summary>
        private static bool FreshTracingFlag()
        {
            try
            {
                string text = File.ReadAllText(GuardConfig.Path);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, "\"tracing\"\\s*:\\s*(true|false)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
            return GuardConfig.Bool("tracing", false);
        }

        private static string PayloadBuild()
        {
            try
            {
                return "build " + File.GetLastWriteTime(typeof(PayloadEntry).Assembly.Location ?? "").ToString("HH:mm:ss");
            }
            catch
            {
                return "(compiled in-memory)";
            }
        }
    }
}
