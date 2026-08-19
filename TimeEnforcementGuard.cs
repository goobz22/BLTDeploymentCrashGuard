using System;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Fixes: after loading a save mid-session while hosting alone, fast-forward stops
    /// working until the game is relaunched.
    ///
    /// Evidence (CrashGuard.log 2026-08-19 00:07-00:08): the co-op mod's
    /// CoopCampaignBehavior.EnforcePlaySpeed runs every campaign tick after an in-game
    /// load and forces TimeControlMode -> UnstoppablePlay, stomping the player's
    /// fast-forward within milliseconds of every click. On a fresh launch it does not
    /// run at all — the enforcement state goes stale on reload. (Method identified
    /// from runtime stack traces in our own log.)
    ///
    /// Guard: a skip-prefix on that method, gated on live peer detection — with a
    /// remote player connected the enforcement runs untouched (their speed sync is
    /// legitimate in real co-op); with nobody connected there is nothing to sync and
    /// the local player keeps control of time. Re-enables automatically the moment a
    /// peer connects.
    /// </summary>
    internal static class TimeEnforcementGuard
    {
        private static bool _applied;
        private static int _lastCheckTick;
        private static bool _peersConnected;
        private static bool _skipLogged;

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                Type behavior = PeerDetection.FindCoopType("CoopCampaignBehavior");
                if (behavior == null)
                {
                    return; // co-op mod absent (or not loaded yet — Apply is retried later)
                }
                int count = 0;
                foreach (MethodInfo method in behavior.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "EnforcePlaySpeed" || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(TimeEnforcementGuard), nameof(Prefix)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[TIME-GUARD] could not patch EnforcePlaySpeed: " + exOne.Message);
                    }
                }
                if (count > 0)
                {
                    _applied = true;
                    Log.Info("[TIME-GUARD] EnforcePlaySpeed peer-gate active (" + count + " method(s)) — solo time control stays with the player");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[TIME-GUARD] apply failed: " + ex.Message);
            }
        }

        private static bool Prefix()
        {
            try
            {
                int now = Environment.TickCount;
                if (_lastCheckTick == 0 || now - _lastCheckTick > 2000 || now < _lastCheckTick)
                {
                    _lastCheckTick = now;
                    bool connected = PeerDetection.AnyRemotePeerConnected() == true;
                    if (connected && !_peersConnected)
                    {
                        _skipLogged = false; // log again if we later return to solo
                        Log.Info("[TIME-GUARD] remote player connected — co-op speed enforcement re-enabled");
                    }
                    _peersConnected = connected;
                }
                if (_peersConnected)
                {
                    return true; // real co-op: their speed sync stays in charge
                }
                if (!_skipLogged)
                {
                    _skipLogged = true;
                    Log.Info("[TIME-GUARD] skipping EnforcePlaySpeed — no remote player connected (auto re-enables when one joins)");
                }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
