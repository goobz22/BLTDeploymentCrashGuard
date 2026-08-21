using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Makes the marriage barter ATOMIC under BannerlordTogether. Decompile-proven money-loss:
    /// BT's MarriageFinalBarterApplyPatch suppresses the native marriage inside the barter and
    /// routes it to host validation — but the sibling barterables (the GOLD you pay) apply
    /// natively in the same BarterManager.ApplyAndFinalizePlayerBarter loop. When BT's gate then
    /// rejects (e.g. "clan mode is synchronized" while its sync is pending), the dowry is gone
    /// and no marriage happened.
    ///
    /// Guard: prefix ApplyAndFinalizePlayerBarter — if the offered barterables include a
    /// MarriageBarterable AND a BT session is active AND BT's clan mode still reads Unknown
    /// (the exact condition its validator rejects on), cancel the WHOLE barter before anything
    /// applies: no gold moves, no marriage attempt, an on-screen line says why. With
    /// ClanModeSoloFix healing the solo case this should never fire alone; it protects the
    /// real co-op window while identity snapshots are still in flight.
    ///
    /// SELF-DISABLING: passes everything through when no BT session is active, when the barter
    /// has no marriage in it, or when clan mode is synchronized — and if BT fixes the ordering
    /// upstream the blocking condition simply never occurs again.
    /// </summary>
    internal static class MarriageBarterGuard
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(BarterManager), "ApplyAndFinalizePlayerBarter");
                if (target == null)
                {
                    Log.Info("[MARRIAGE-GUARD] BarterManager.ApplyAndFinalizePlayerBarter not found — guard inactive (game update?)");
                    Diag.Report("marriage-barter-guard", false, "ApplyAndFinalizePlayerBarter not found");
                    return;
                }
                harmony.Patch(target, new HarmonyMethod(typeof(MarriageBarterGuard), nameof(Prefix)));
                Log.Info("[MARRIAGE-GUARD] marriage barter is atomic — if BT would block the marriage, the barter cancels BEFORE any gold moves");
                Diag.Report("marriage-barter-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[MARRIAGE-GUARD] apply failed: " + ex.Message);
                Diag.Report("marriage-barter-guard", false, ex.Message);
            }
        }

        private static bool Prefix(Hero offererHero, Hero otherHero, BarterData barterData)
        {
            try
            {
                if (barterData == null)
                {
                    return true;
                }
                bool hasMarriage = false;
                List<Barterable> offered = barterData.GetOfferedBarterables();
                if (offered != null)
                {
                    foreach (Barterable barterable in offered)
                    {
                        if (barterable is MarriageBarterable)
                        {
                            hasMarriage = true;
                            break;
                        }
                    }
                }
                if (!hasMarriage)
                {
                    return true;
                }
                if (PeerDetection.ReadCoopStaticBool("IsActive") != true)
                {
                    return true; // no BT session — vanilla marriage, nothing to protect against
                }
                byte? mode = ClanModeSoloFix.ReadLiveMode();
                if (mode == null || mode.Value != 0 /* af.bI Unknown — the value BT's validator rejects */)
                {
                    return true; // clan mode synchronized (or unreadable) — let the barter apply
                }
                SelfHealing.RecordFire("marriage-barter-guard");
                Log.Info("[MARRIAGE-GUARD] BLOCKED a marriage barter while BT clan mode is Unknown — cancelled whole barter, no gold taken (offerer=" + Name(offererHero) + " other=" + Name(otherHero) + ")");
                Log.Screen("marriage barter cancelled BEFORE any gold moved — co-op clan sync isn't ready yet, try again in a moment");
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("[MARRIAGE-GUARD] prefix error, passing through: " + ex.Message);
                return true;
            }
        }

        private static string Name(Hero hero)
        {
            try
            {
                return hero != null && hero.Name != null ? hero.Name.ToString() : "null";
            }
            catch
            {
                return "?";
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool targetExists = AccessTools.Method(typeof(BarterManager), "ApplyAndFinalizePlayerBarter") != null;
            bool passThroughOnNull = Prefix(null, null, null);
            bool pass = targetExists && passThroughOnNull;
            return SelfHealing.TestResult.Of("marriage-barter-guard.contract", pass,
                pass ? "target re-resolved; prefix passes through on null barter"
                     : "targetExists=" + targetExists + " passThroughOnNull=" + passThroughOnNull + " (game update?)");
        }
    }
}
