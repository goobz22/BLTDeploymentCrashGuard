using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards the NRE that CTD'd on an issue/quest "OK" click (field crash 2026-08-21 22:25):
    /// IssueManager.MakeAlternativeTroopsReturn -> Hero.ChangeState -> OnHeroActivatedEvent ->
    /// CharacterDevelopmentCampaignBehavior.OnHeroActivated, which calls
    /// hero.HeroDeveloper.DevelopCharacterStats(). When an issue reactivates a hero whose
    /// HeroDeveloper was never initialized (a half-built/edge-case hero), that dereference NREs,
    /// and because it runs synchronously inside the affirmative-action handler it takes the whole
    /// click down to desktop.
    ///
    /// We cannot fix WHY the hero has no developer (deep in TaleWorlds issue-quest code), but we can
    /// stop the crash: a self-disabling finalizer on OnHeroActivated. It is inert unless the handler
    /// throws; on an escaping exception it logs and swallows so the hero-activated event's other
    /// listeners and the troops-return action complete instead of CTD. Perk development is simply
    /// skipped for that one broken hero. If TaleWorlds fixes the underlying init, the exception stops
    /// and this guard is permanently inert (visible as never-fired in the health report).
    /// </summary>
    internal static class HeroActivationCrashGuard
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type behavior = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.CharacterDevelopmentCampaignBehavior");
                var method = behavior != null ? AccessTools.Method(behavior, "OnHeroActivated") : null;
                if (method == null)
                {
                    Log.Info("[HEROACT-GUARD] CharacterDevelopmentCampaignBehavior.OnHeroActivated not found — guard inactive (game update?)");
                    Diag.Report("hero-activation-guard", false, "OnHeroActivated not found");
                    return;
                }
                harmony.Patch(method, null, null, null, new HarmonyMethod(typeof(HeroActivationCrashGuard), nameof(Finalizer)));
                Log.Info("[HEROACT-GUARD] hero-activation crash guard active (self-disables if the underlying NRE stops occurring)");
                Diag.Report("hero-activation-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[HEROACT-GUARD] apply failed: " + ex.Message);
                Diag.Report("hero-activation-guard", false, ex.Message);
            }
        }

        private static Exception Finalizer(Exception __exception, Hero hero)
        {
            if (__exception == null)
            {
                return null; // no bug this time — guard inert
            }
            SelfHealing.RecordFire("hero-activation-guard");
            string who = DescribeHero(hero);
            Log.Info("[HEROACT-GUARD] SUPPRESSED crash in OnHeroActivated for " + who + " (likely null HeroDeveloper on a half-built hero): " + __exception.Message);
            Log.Screen("prevented a crash while a quest returned troops — that hero's perks were skipped");
            return null; // swallow — let the event's other listeners and the quest action finish
        }

        private static string DescribeHero(Hero hero)
        {
            try
            {
                if (hero == null)
                {
                    return "null hero";
                }
                string name = hero.Name != null ? hero.Name.ToString() : hero.StringId;
                bool devNull = hero.HeroDeveloper == null;
                return name + (devNull ? " (HeroDeveloper=null)" : "");
            }
            catch
            {
                return "hero(?)";
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type behavior = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.CharacterDevelopmentCampaignBehavior");
            bool methodExists = behavior != null && AccessTools.Method(behavior, "OnHeroActivated") != null;
            bool inertOnNull = Finalizer(null, null) == null;
            bool pass = methodExists && inertOnNull;
            return SelfHealing.TestResult.Of("hero-activation-guard.contract", pass,
                pass ? "target re-resolved; finalizer inert on null exception"
                     : "methodExists=" + methodExists + " inertOnNull=" + inertOnNull + " (game update?)");
        }
    }
}
