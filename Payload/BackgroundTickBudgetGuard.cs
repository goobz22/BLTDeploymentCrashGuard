using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Fixes the whole-game freeze during host battles (field hang 2026-08-30 ~15:24, live
    /// stack samples via debugger attach): BT's CoopSubModule.TryBackgroundCampaignTick runs
    /// Campaign.RealTick+Tick on EVERY application tick while the host is in a mission
    /// (ShouldBackgroundTick: active state is not the map but a MapState is in the stack).
    /// The method has no time budget — when a campaign tick becomes pathologically expensive
    /// (observed: a third army joining the player's ongoing battle put
    /// EncounterManager.HandleEncounters + BattleSyncBehavior encounter-hold checks +
    /// hourly-AI catch-up into multi-second ticks, all 16 cores pegged), every frame drowns
    /// in background campaign work and the game is unresponsive for minutes to forever.
    ///
    /// Fix: EQUAL-TIME THROTTLE, not a disable — co-op keeps its background world. After a
    /// background tick that exceeds the budget, background ticking pauses for as long as
    /// that tick took (capped), so the foreground always gets ~half of wall time: the
    /// mission loads, the UI repaints, the player keeps control. Under normal load
    /// (sub-budget ticks) the guard changes nothing. Skipping a call is safe by
    /// construction — BT's own method starts with many unconditional early-outs
    /// (paused / saving / not host), so callers already tolerate no-op ticks.
    ///
    /// Self-disabling: never fires while background ticks stay under budget; fires are
    /// counted and rate-limit logged as [TICK-GUARD] for field evidence (upstream report:
    /// UPSTREAM_BUG_REPORT.md "background tick has no time budget").
    /// </summary>
    internal static class BackgroundTickBudgetGuard
    {
        /// <summary>A background campaign tick may cost this much per application tick
        /// before throttling kicks in. Well above normal ticks (a few ms), well below
        /// freeze territory.</summary>
        private const long BudgetMs = 100;

        /// <summary>Never block background ticking longer than this, no matter how long a
        /// tick took — the co-op world must keep moving.</summary>
        private const long MaxBlockMs = 10000;

        private static bool _applied;
        private static long _startTimestamp;
        private static long _blockedUntilTimestamp;
        private static long _worstMs;
        private static int _throttledCalls;
        private static int _lastLogTick;

        internal static void Apply(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                Type coop = AccessTools.TypeByName("BannerlordTogether.CoopSubModule");
                if (coop == null)
                {
                    // BT not loaded (yet) — the module-screen retry calls again; vanilla needs no guard.
                    // Report either way so the health line never silently omits this component
                    // (a later successful retry re-reports and replaces this entry).
                    bool btLoaded = PeerDetection.IsCoopAssemblyLoaded();
                    Diag.Report("bg-tick-budget-guard", !btLoaded,
                        btLoaded ? "BannerlordTogether.CoopSubModule not found (renamed?)" : "inert — BannerlordTogether not loaded",
                        critical: btLoaded);
                    return;
                }
                MethodInfo tick = AccessTools.Method(coop, "TryBackgroundCampaignTick");
                if (tick == null)
                {
                    Log.Info("[TICK-GUARD] CoopSubModule.TryBackgroundCampaignTick not found — guard inactive (BT update?)");
                    Diag.Report("bg-tick-budget-guard", false, "TryBackgroundCampaignTick not resolved", critical: true);
                    return;
                }
                harmony.Patch(tick,
                    new HarmonyMethod(typeof(BackgroundTickBudgetGuard), nameof(Prefix)),
                    new HarmonyMethod(typeof(BackgroundTickBudgetGuard), nameof(Postfix)));
                _applied = true;
                Log.Info("[TICK-GUARD] background-campaign-tick budget guard active (budget " + BudgetMs +
                         "ms, equal-time backoff capped at " + MaxBlockMs + "ms)");
                Diag.Report("bg-tick-budget-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[TICK-GUARD] apply failed: " + ex.Message);
                Diag.Report("bg-tick-budget-guard", false, ex.Message);
            }
        }

        /// <summary>How long to block background ticking after a tick that took elapsedMs.
        /// Zero under budget; equal-time (capped) above it — the foreground is guaranteed
        /// about half of wall time however heavy the campaign gets.</summary>
        internal static long ComputeBlockMs(long elapsedMs)
        {
            if (elapsedMs <= BudgetMs)
            {
                return 0;
            }
            return Math.Min(elapsedMs, MaxBlockMs);
        }

        /// <summary>The frame delta BT hands to the campaign — IL (2026-09-04): OnApplicationTick passes
        /// its dt straight through, exactly as vanilla MapState.OnMapModeTick does, and
        /// Campaign.TickMapTime advances game time by 0.25 × dt × speed. Logged with every throttle
        /// line so the field evidence shows whether a heavy tick was fed a long frame.</summary>
        private static float _lastDt;

        private static bool Prefix(float dt)
        {
            _lastDt = dt;
            if (Stopwatch.GetTimestamp() < _blockedUntilTimestamp)
            {
                _throttledCalls++;
                return false; // still paying back the last heavy tick — give this frame to the game
            }
            _startTimestamp = Stopwatch.GetTimestamp();
            return true;
        }

        private static void Postfix()
        {
            try
            {
                if (_startTimestamp == 0)
                {
                    return; // this call was skipped by the prefix
                }
                long elapsedMs = (Stopwatch.GetTimestamp() - _startTimestamp) * 1000 / Stopwatch.Frequency;
                _startTimestamp = 0;
                long blockMs = ComputeBlockMs(elapsedMs);
                if (blockMs == 0)
                {
                    return;
                }
                _blockedUntilTimestamp = Stopwatch.GetTimestamp() + blockMs * Stopwatch.Frequency / 1000;
                if (elapsedMs > _worstMs)
                {
                    _worstMs = elapsedMs;
                }
                SelfHealing.RecordFire("bg-tick-budget-guard");
                int now = Environment.TickCount;
                if (_lastLogTick == 0 || now - _lastLogTick >= 5000 || now < _lastLogTick)
                {
                    _lastLogTick = now;
                    Log.Info("[TICK-GUARD] BT background campaign tick took " + elapsedMs + "ms (frame dt " + _lastDt.ToString("F3") + "s; budget " + BudgetMs +
                             "ms) — pausing background ticking " + blockMs + "ms so the game stays responsive" +
                             " (worst " + _worstMs + "ms, " + _throttledCalls + " throttled call(s) this session)");
                }
            }
            catch
            {
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type coop = AccessTools.TypeByName("BannerlordTogether.CoopSubModule");
            bool resolved = coop == null || AccessTools.Method(coop, "TryBackgroundCampaignTick") != null;
            // The throttle decision itself: inert under budget, equal-time above, hard cap.
            bool decision = ComputeBlockMs(BudgetMs) == 0 &&
                            ComputeBlockMs(BudgetMs + 1) == BudgetMs + 1 &&
                            ComputeBlockMs(3000) == 3000 &&
                            ComputeBlockMs(120000) == MaxBlockMs;
            bool pass = resolved && decision;
            return SelfHealing.TestResult.Of("bg-tick-budget-guard.contract", pass,
                pass ? (coop == null ? "BT absent (vanilla) — inert; decision logic correct" : "target re-resolved; decision logic correct")
                     : "resolved=" + resolved + " decision=" + decision);
        }
    }
}
