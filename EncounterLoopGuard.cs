using System;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Breaks the infinite conversation/meeting loop (2026-08-19 20:07-20:08):
    /// after the player leaves an encounter meeting, PlayerEncounter.Finish runs, and
    /// on the next campaign tick the co-op sync layer re-applies a stuck pending
    /// encounter request (BattleSyncBehavior.ProcessPendingClientEncounterRequests ->
    /// ApplyEncounterRequestNow -> StartPartyEncounter -> RestartPlayerEncounter),
    /// reopening the same encounter_meeting menu forever — the queue entry is never
    /// consumed. (Method names from runtime stack traces in CrashGuard.log.)
    ///
    /// Guard: rate-based loop breaker on ApplyEncounterRequestNow. Legitimate
    /// requests are rare, player-initiated events; 4+ applications within 15 seconds
    /// only happens in the pathological loop. Once tripped, applications are
    /// suppressed; after 60 seconds of suppression one retry is allowed through, so
    /// the system self-recovers when the stuck entry finally clears (and re-trips if
    /// it has not).
    /// </summary>
    internal static class EncounterLoopGuard
    {
        private const int TripCount = 4;
        private const int WindowMs = 15000;
        private const int RetryAfterMs = 60000;
        private const int FinishChainMs = 4000;

        private static bool _applied;
        private static readonly int[] _recentCalls = new int[TripCount];
        private static int _recentIndex;
        private static bool _tripped;
        private static int _lastSuppressedTick;
        private static int _lastFinishTick;

        /// <summary>Stamped by the PlayerEncounter.Finish tracer. The infinite-loop
        /// signature is finish -> immediate re-application; only applications that
        /// closely follow a local Finish count toward tripping, so legitimate join
        /// requests (which have no preceding local Finish) are NEVER suppressed —
        /// the v1 pure-rate breaker could eat a partner's join storm.</summary>
        internal static void NoteEncounterFinish()
        {
            _lastFinishTick = Environment.TickCount;
        }

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                Type battleSync = PeerDetection.FindCoopType("BattleSyncBehavior");
                if (battleSync == null)
                {
                    return; // co-op mod absent or not loaded yet — Apply is retried later
                }
                int count = 0;
                foreach (MethodInfo method in battleSync.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "ApplyEncounterRequestNow" || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(EncounterLoopGuard), nameof(Prefix)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[ENCOUNTER-GUARD] could not patch ApplyEncounterRequestNow: " + exOne.Message);
                    }
                }
                if (count > 0)
                {
                    _applied = true;
                    Log.Info("[ENCOUNTER-GUARD] encounter-request loop breaker active (" + count + " method(s))");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[ENCOUNTER-GUARD] apply failed: " + ex.Message);
            }
        }

        private static bool Prefix()
        {
            try
            {
                int now = Environment.TickCount;
                if (_tripped)
                {
                    if (now - _lastSuppressedTick > RetryAfterMs || now < _lastSuppressedTick)
                    {
                        _tripped = false;
                        Log.Info("[ENCOUNTER-GUARD] retry window — letting one encounter request through");
                        // fall through to normal counting; this call is allowed
                    }
                    else
                    {
                        _lastSuppressedTick = now;
                        return false; // still looping — keep it suppressed
                    }
                }

                bool followsFinish = _lastFinishTick != 0 && now - _lastFinishTick < FinishChainMs && now >= _lastFinishTick;
                if (!followsFinish)
                {
                    return true; // not the loop signature — never block ordinary/join applications
                }
                int oldest = _recentCalls[_recentIndex];
                _recentCalls[_recentIndex] = now;
                _recentIndex = (_recentIndex + 1) % TripCount;
                if (oldest != 0 && now - oldest < WindowMs && now >= oldest)
                {
                    _tripped = true;
                    _lastSuppressedTick = now;
                    Log.Info("[ENCOUNTER-GUARD] LOOP BROKEN: " + TripCount + " encounter-request applications within " + (WindowMs / 1000) + "s — suppressing (auto-retry every " + (RetryAfterMs / 1000) + "s)");
                    Log.Screen("broke a stuck encounter loop (details in CrashGuard.log)");
                    return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
