using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards client character creation against a half-synced world.
    ///
    /// Crash (2026-08-19, client hero creation, picking a culture and advancing):
    /// NRE in DefaultSettlementValueModel.FindFarthestDistanceBetweenSettlementsInClan
    /// via FindMostSuitableHomeSettlement &lt;- Clan.ResetPlayerHomeAndFactionMidSettlement
    /// &lt;- CharacterCreationContent.ApplyCulture. The method dereferences
    /// clan.MapFaction.FactionMidSettlement (passing it to MapDistanceModel.GetDistance),
    /// which is null on a client whose faction/settlement graph has not finished
    /// replicating. Native has no guard because in single-player the graph is always
    /// complete here.
    ///
    /// Fix: finalizer on the public FindMostSuitableHomeSettlement — on any escaping
    /// exception, return a safe home settlement of the same shape the method itself
    /// returns in its own edge cases (InitialHomeSettlement, else the first settlement),
    /// so culture application completes and character creation proceeds.
    /// </summary>
    internal static class ClientHeroCreationGuard
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(DefaultSettlementValueModel), "FindMostSuitableHomeSettlement", new[] { typeof(Clan) });
                if (method == null)
                {
                    Log.Info("[HEROCREATE-GUARD] FindMostSuitableHomeSettlement not found — guard inactive");
                    return;
                }
                harmony.Patch(method, null, null, null, new HarmonyMethod(typeof(ClientHeroCreationGuard), nameof(HomeSettlementFinalizer)));
                Log.Info("[HEROCREATE-GUARD] home-settlement crash guard active");
            }
            catch (Exception ex)
            {
                Log.Info("[HEROCREATE-GUARD] apply failed: " + ex.Message);
            }
        }

        private static Exception HomeSettlementFinalizer(Exception __exception, Clan clan, ref Settlement __result)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SelfHealing.RecordFire("hero-creation-guard");
                Settlement fallback = null;
                if (clan != null)
                {
                    fallback = clan.InitialHomeSettlement;
                }
                if (fallback == null && Settlement.All != null && Settlement.All.Count > 0)
                {
                    fallback = Settlement.All[0];
                }
                __result = fallback;
                Log.Info("[HEROCREATE-GUARD] SUPPRESSED crash in FindMostSuitableHomeSettlement (half-synced clan/faction) — returned fallback home=" +
                         (fallback != null ? fallback.Name.ToString() : "null") + "; detail: " + __exception.Message);
                Log.Screen("prevented a hero-creation crash (half-synced world) — continuing");
            }
            catch (Exception exRecovery)
            {
                Log.Info("[HEROCREATE-GUARD] recovery failed: " + exRecovery.Message);
                __result = null;
            }
            return null;
        }
    }
}
