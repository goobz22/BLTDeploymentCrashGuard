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

                // Attribute-based patches (the two deployment crash finalizers) for THIS assembly.
                harmony.PatchAll(typeof(PayloadEntry).Assembly);

                // Always-on guards and fixes.
                TimeFlowPatch.Apply(harmony);
                PartyAiCrashGuard.Apply(harmony);
                EncounterLoopGuard.Apply(harmony);
                MapClickSpeedKeeper.Apply(harmony);
                ClientHeroCreationGuard.Apply(harmony);
                ClanScreenCrashGuard.Apply(harmony);

                // Client bootstrap fix — must beat BT's first (and only) verify on a fresh process;
                // on a mid-game reload it just installs the prefix (BT won't verify again).
                ClientBootstrapFix.Apply(harmony);
                TimeEnforcementGuard.Apply(harmony);

                // Verbose tracers — off unless troubleshooting.
                if (GuardConfig.Bool("tracing", false))
                {
                    TracePatches.Apply(harmony);
                    ControlTrace.Apply(harmony);
                    TimeTrace.Apply(harmony);
                    CoopBattleTrace.Apply(harmony);
                    RoleTrace.Apply(harmony);
                    Log.Info("tracing ENABLED (guardconfig tracing=true)");
                }

                // Clear a stale co-op action cache from a previous aborted session.
                BootstrapWatch.CheckAtStartup();

                // Establish battle mode now (so a mid-game reload re-decides immediately).
                BattleMode.DecideAndApply(harmony, "apply");

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
                throw; // let the harness keep the previous generation on a failed apply
            }
        }

        public void OnBeforeInitialModuleScreen()
        {
            ClientBootstrapFix.Apply(Harmony); // retry in case the co-op assembly loaded late
            TimeEnforcementGuard.Apply(Harmony);
            BattleMode.DecideAndApply(Harmony, "module-screen");
        }

        public void OnGameStart()
        {
            TimeEnforcementGuard.Apply(Harmony);
            EncounterLoopGuard.Apply(Harmony);
            BattleMode.DecideAndApply(Harmony, "game-start");
        }

        public void OnMissionInit()
        {
            BattleMode.DecideAndApply(Harmony, "mission-init");
        }

        public void Tick()
        {
            RefreshRole();
            PlayerIdentityGuard.Tick();
            ShareTimeControl.Tick();
            RoleTrace.Tick();
            LogStreamer.Tick();
            BootstrapWatch.Tick();
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
