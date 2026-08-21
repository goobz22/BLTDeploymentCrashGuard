using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Blocks the "main hero dies of sickness" mechanic at its ROOT. Decompile-proven vanilla
    /// flow (AgingCampaignBehavior): once the main hero is age >= BecomeOldAge (55), every daily
    /// tick calls IsItTimeOfDeath which rolls ProbabilityOfDeath; on a hit the main hero "Caught
    /// Illness" (MainHeroIllDays -1 -> 0; IsMainHeroIll == days != -1). DailyTickHero then
    /// increments ill days; past day 3 it drains HP 5%*days daily, and at &lt;= 1 HP kills via
    /// KillMainHeroWithIllness (unless an extra life is consumed). In co-op that ends a shared
    /// campaign for one player, so with "noSickness" (default true):
    ///
    ///  1. IsItTimeOfDeath prefix — the local main hero never rolls the old-age/illness death at
    ///     all (root cause: the illness is never caught). NPC lords age and die normally.
    ///  2. DailyTickHero prefix — an ALREADY-ill main hero (e.g. a save from before this guard)
    ///     is cured outright: ill days reset to -1 and a pending DiedOfOldAge death mark is
    ///     cleared, then vanilla runs normally as healthy. No skipped aging/come-of-age events,
    ///     no permanently-stuck ill flag (the trap in the third-party NoSickness mod's approach,
    ///     which skips the whole method on ill days and never clears the flag).
    ///
    /// Coexists safely with the third-party NoSickness mod (we never increment ill days; once we
    /// cure, its prefix sees a healthy hero and passes through). Each machine protects its own
    /// player, so both co-op players are covered by running this mod.
    /// </summary>
    internal static class IllnessDeathGuard
    {
        private static bool _enabled;
        private static bool _rollBlockLogged;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _enabled = GuardConfig.Bool("noSickness", true);
                MethodBase roll = ResolveRoll();
                MethodBase tick = ResolveTick();
                if (roll == null || tick == null)
                {
                    Log.Info("[NOSICK] AgingCampaignBehavior methods not found (roll=" + (roll != null) + " tick=" + (tick != null) + ") — guard inactive (game update?)");
                    Diag.Report("illness-death-guard", false, "AgingCampaignBehavior target missing");
                    return;
                }
                if (!_enabled)
                {
                    Log.Info("[NOSICK] disabled by config (noSickness: false)");
                    Diag.Report("illness-death-guard", true, "disabled by config");
                    return;
                }
                harmony.Patch(roll, new HarmonyMethod(typeof(IllnessDeathGuard), nameof(IsItTimeOfDeathPrefix)));
                harmony.Patch(tick, new HarmonyMethod(typeof(IllnessDeathGuard), nameof(DailyTickHeroPrefix)));
                Log.Info("[NOSICK] illness-death guard active — the local player can no longer catch or die of the old-age sickness");
                Diag.Report("illness-death-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[NOSICK] apply failed: " + ex.Message);
                Diag.Report("illness-death-guard", false, ex.Message);
            }
        }

        private static MethodBase ResolveRoll()
        {
            Type aging = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.AgingCampaignBehavior");
            return aging != null ? AccessTools.Method(aging, "IsItTimeOfDeath") : null;
        }

        private static MethodBase ResolveTick()
        {
            Type aging = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.AgingCampaignBehavior");
            return aging != null ? AccessTools.Method(aging, "DailyTickHero") : null;
        }

        /// <summary>Root block: the local main hero never rolls the old-age/illness death.</summary>
        private static bool IsItTimeOfDeathPrefix(Hero hero)
        {
            try
            {
                if (!_enabled || hero == null || hero != Hero.MainHero)
                {
                    return true;
                }
                if (!_rollBlockLogged)
                {
                    _rollBlockLogged = true;
                    SelfHealing.RecordFire("illness-death-guard");
                    Log.Info("[NOSICK] blocking the daily old-age/illness death roll for " + hero.Name + " (age " + (int)hero.Age + ") — logged once, active every day");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("[NOSICK] roll prefix error, passing through: " + ex.Message);
                return true;
            }
        }

        /// <summary>Cure an illness already in progress (a save from before the guard).</summary>
        private static bool DailyTickHeroPrefix(Hero hero)
        {
            try
            {
                if (!_enabled || hero == null || hero != Hero.MainHero)
                {
                    return true;
                }
                Campaign campaign = Campaign.Current;
                if (campaign == null || !Hero.IsMainHeroIll)
                {
                    return true;
                }
                campaign.MainHeroIllDays = -1;
                if (hero.DeathMark == TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge)
                {
                    // KillMainHeroWithIllness sets this mark; clear it (private setter) so the
                    // ApplyByDeathMark branch at the top of DailyTickHero can't finish the kill.
                    AccessTools.PropertySetter(typeof(Hero), "DeathMark")?.Invoke(hero,
                        new object[] { TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail.None });
                }
                SelfHealing.RecordFire("illness-death-guard");
                Log.Info("[NOSICK] CURED the in-progress illness of " + hero.Name + " (ill days reset, death mark cleared if set)");
                Log.Screen("your sickness was cured (no-sickness guard)");
                return true; // vanilla proceeds as a healthy hero — aging events untouched
            }
            catch (Exception ex)
            {
                Log.Info("[NOSICK] tick prefix error, passing through: " + ex.Message);
                return true;
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            // Re-resolve both targets by name so a game update that renames/moves them reddens
            // this test (the resolve at Apply time is not reused). Also prove both prefixes pass
            // through (return true) for a null hero — the only input testable outside a campaign.
            bool rollExists = ResolveRoll() != null;
            bool tickExists = ResolveTick() != null;
            bool passThrough = IsItTimeOfDeathPrefix(null) && DailyTickHeroPrefix(null);
            bool pass = rollExists && tickExists && passThrough;
            return SelfHealing.TestResult.Of("illness-death-guard.contract", pass,
                pass ? "both targets re-resolved; prefixes pass through on null hero"
                     : "rollExists=" + rollExists + " tickExists=" + tickExists + " passThrough=" + passThrough + " (game update?)");
        }
    }
}
