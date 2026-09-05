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
    /// Guard: signature-gated loop breaker on ApplyEncounterRequestNow. The loop
    /// signature is "local PlayerEncounter.Finish, then an application within
    /// FinishChainMs"; only such applications count, so a partner's legitimate join
    /// storm (no preceding local Finish) is NEVER suppressed — the v1 pure-rate
    /// breaker could eat it. 4 signature hits within 15 s trips the breaker; after
    /// 60 s of suppression one retry is allowed through, so the system self-recovers
    /// when the stuck entry finally clears (and re-trips if it has not).
    ///
    /// The Finish stamp is hooked HERE, always-on. Until v1.3.2 it was written only by
    /// the [TRACE] tracer's PlayerEncounter.Finish hook, so with tracing=false (the
    /// default) the breaker could never trip (audit 2026-09-04).
    /// </summary>
    internal static class EncounterLoopGuard
    {
        private const string Component = "encounter-loop-guard";
        private const string Tag = "[ENCOUNTER-GUARD]";
        private const string PlayerEncounterType = "TaleWorlds.CampaignSystem.Encounters.PlayerEncounter";
        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal const int TripCount = 4;
        internal const int WindowMs = 15000;
        internal const int RetryAfterMs = 60000;
        internal const int FinishChainMs = 4000;

        private static bool _applied;
        private static bool _testRegistered;
        private static bool _reported;
        private static bool _finishHooked;
        private static readonly int[] _recentCalls = new int[TripCount];
        private static int _recentIndex;
        private static bool _tripped;
        private static int _lastSuppressedTick;
        private static int _lastFinishTick;

        /// <summary>Stamped by our own PlayerEncounter.Finish prefix (and harmlessly by anything
        /// else that wants to note a local finish).</summary>
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
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                if (!_finishHooked)
                {
                    _finishHooked = PatchFinish(harmony) > 0;
                }
                Type battleSync = PeerDetection.FindCoopType("BattleSyncBehavior");
                if (battleSync == null)
                {
                    // Apply is retried from the module screen / game start (BT may load late).
                    // Report once so the health summary is never silent about this guard.
                    if (!_reported)
                    {
                        _reported = true;
                        bool btLoaded = PeerDetection.IsCoopAssemblyLoaded();
                        Diag.Report(Component, !btLoaded, btLoaded ? "BattleSyncBehavior not found (BannerlordTogether renamed it?)" : "inert — BannerlordTogether not loaded");
                        if (btLoaded)
                        {
                            Log.Info(Tag + " BattleSyncBehavior not found — the loop breaker is inactive (BannerlordTogether update?)");
                        }
                    }
                    return;
                }
                int count = 0;
                foreach (MethodInfo method in battleSync.GetMethods(AllDeclared))
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
                        Log.Info(Tag + " could not patch ApplyEncounterRequestNow: " + exOne.Message);
                    }
                }
                if (count > 0)
                {
                    _applied = true;
                    if (!_reported)
                    {
                        _reported = true;
                        Diag.Report(Component, _finishHooked, _finishHooked ? "" : "PlayerEncounter.Finish not hooked — loop signature unavailable");
                    }
                    Log.Info(Tag + " encounter-request loop breaker active (" + count + " method(s); local-Finish stamp hooked=" + _finishHooked + ")");
                }
                else if (!_reported)
                {
                    _reported = true;
                    Diag.Report(Component, false, "ApplyEncounterRequestNow not found on BattleSyncBehavior");
                    Log.Info(Tag + " ApplyEncounterRequestNow not found — the loop breaker is inactive (BannerlordTogether update?)");
                }
            }
            catch (Exception ex)
            {
                if (!_reported)
                {
                    _reported = true;
                    Diag.Report(Component, false, ex.Message);
                }
                Log.Info(Tag + " apply failed: " + ex.Message);
            }
        }

        private static int PatchFinish(Harmony harmony)
        {
            int count = 0;
            try
            {
                Type playerEncounter = AccessTools.TypeByName(PlayerEncounterType);
                if (playerEncounter == null)
                {
                    Log.Info(Tag + " PlayerEncounter type not found — local-Finish stamp unavailable");
                    return 0;
                }
                foreach (MethodInfo method in playerEncounter.GetMethods(AllDeclared))
                {
                    if (method.Name != "Finish" || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(EncounterLoopGuard), nameof(FinishPrefix)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info(Tag + " could not hook PlayerEncounter.Finish: " + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " Finish hook failed: " + ex.Message);
            }
            return count;
        }

        private static void FinishPrefix()
        {
            NoteEncounterFinish();
        }

        /// <summary>Loop signature: an application closely following a LOCAL Finish.</summary>
        internal static bool FollowsFinish(int now, int lastFinishTick)
        {
            return lastFinishTick != 0 && now - lastFinishTick < FinishChainMs && now >= lastFinishTick;
        }

        /// <summary>TripCount signature hits inside WindowMs (oldest = the slot being overwritten).</summary>
        internal static bool WindowTripped(int now, int oldest)
        {
            return oldest != 0 && now - oldest < WindowMs && now >= oldest;
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
                        Log.Info(Tag + " retry window — letting one encounter request through");
                        // fall through to normal counting; this call is allowed
                    }
                    else
                    {
                        _lastSuppressedTick = now;
                        return false; // still looping — keep it suppressed
                    }
                }

                if (!FollowsFinish(now, _lastFinishTick))
                {
                    return true; // not the loop signature — never block ordinary/join applications
                }
                int oldest = _recentCalls[_recentIndex];
                _recentCalls[_recentIndex] = now;
                _recentIndex = (_recentIndex + 1) % TripCount;
                if (WindowTripped(now, oldest))
                {
                    _tripped = true;
                    _lastSuppressedTick = now;
                    SelfHealing.RecordFire(Component);
                    Log.Info(Tag + " LOOP BROKEN: " + TripCount + " encounter-request applications within " + (WindowMs / 1000) + "s of local encounter finishes — suppressing (auto-retry every " + (RetryAfterMs / 1000) + "s)");
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

        private static bool HasMethod(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods(AllDeclared))
            {
                if (method.Name == name && !method.IsAbstract)
                {
                    return true;
                }
            }
            return false;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type playerEncounter = AccessTools.TypeByName(PlayerEncounterType);
            bool finish = playerEncounter != null && HasMethod(playerEncounter, "Finish");
            bool btLoaded = PeerDetection.IsCoopAssemblyLoaded();
            Type battleSync = PeerDetection.FindCoopType("BattleSyncBehavior");
            bool target = !btLoaded || (battleSync != null && HasMethod(battleSync, "ApplyEncounterRequestNow"));
            bool logic =
                !FollowsFinish(1000, 0) &&          // no local finish ever -> never the signature
                FollowsFinish(5000, 2000) &&        // 3 s after a finish -> signature
                !FollowsFinish(7000, 2000) &&       // 5 s after -> not the signature
                WindowTripped(10000, 1000) &&       // 4th hit 9 s after the oldest -> trip
                !WindowTripped(20000, 1000) &&      // 19 s -> no trip
                !WindowTripped(5000, 0);            // empty slot -> no trip
            bool pass = finish && target && logic;
            return SelfHealing.TestResult.Of("encounter-loop-guard.contract", pass,
                pass ? "PlayerEncounter.Finish + " + (btLoaded ? "BattleSyncBehavior.ApplyEncounterRequestNow" : "(BT not loaded)") + " re-resolved; loop-signature logic verified"
                     : "finish=" + finish + " target=" + target + " logic=" + logic);
        }
    }
}
