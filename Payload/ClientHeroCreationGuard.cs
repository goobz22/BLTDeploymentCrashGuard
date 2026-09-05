using System;
using System.Reflection;
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
        internal const string Component = "hero-creation-guard";
        private const string Tag = "[HEROCREATE-GUARD]";
        private static bool _applied;

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            _applied = true;
            try
            {
                SelfHealing.RegisterTest(SelfTest);
                MethodInfo method = AccessTools.Method(typeof(DefaultSettlementValueModel), "FindMostSuitableHomeSettlement", new[] { typeof(Clan) });
                if (method == null)
                {
                    Diag.Report(Component, false, "FindMostSuitableHomeSettlement(Clan) not found");
                    Log.Info(Tag + " FindMostSuitableHomeSettlement not found — guard inactive (game update?)");
                    return;
                }
                harmony.Patch(method, null, null, null, new HarmonyMethod(typeof(ClientHeroCreationGuard), nameof(HomeSettlementFinalizer)));
                Diag.Report(Component, true, "");
                Log.Info(Tag + " home-settlement crash guard active");
            }
            catch (Exception ex)
            {
                Diag.Report(Component, false, ex.Message);
                Log.Info(Tag + " apply failed: " + ex.Message);
            }
        }

        internal static Exception HomeSettlementFinalizer(Exception __exception, Clan clan, ref Settlement __result)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SelfHealing.RecordFire(Component);
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
                Log.Info(Tag + " SUPPRESSED crash in FindMostSuitableHomeSettlement (half-synced clan/faction) — returned fallback home=" +
                         (fallback != null ? fallback.Name.ToString() : "null") + "; detail: " + __exception.Message);
                Log.Screen("prevented a hero-creation crash (half-synced world) — continuing");
            }
            catch (Exception exRecovery)
            {
                Log.Info(Tag + " recovery failed: " + exRecovery.Message);
                __result = null;
            }
            return null;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = AccessTools.Method(typeof(DefaultSettlementValueModel), "FindMostSuitableHomeSettlement", new[] { typeof(Clan) }) != null;
            Settlement result = null;
            bool inert = HomeSettlementFinalizer(null, null, ref result) == null && result == null;
            bool pass = resolved && inert;
            return SelfHealing.TestResult.Of("hero-creation-guard.contract", pass,
                pass ? "target re-resolved; finalizer inert on null exception" : "resolved=" + resolved + " inert=" + inert);
        }
    }
}
