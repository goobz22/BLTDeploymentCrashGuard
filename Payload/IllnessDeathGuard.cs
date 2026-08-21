using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Blocks the vanilla "main hero dies of illness" outcome (AgingCampaignBehavior.DailyTickHero:
    /// once the main hero has been ill &gt; 3 days at &lt;= 1 HP, the game rolls a death). In co-op a
    /// sickness death ends a whole shared campaign for one player, so this guard takes over the
    /// ill-day tick for the LOCAL main hero and cures the illness cycle instead of letting the
    /// death branch run. Each machine protects its own player, so with the mod installed on both
    /// sides both players are covered.
    ///
    /// Config: "noSickness" (default true).
    ///
    /// SELF-DISABLING vs the third-party "NoSickness" mod: if that mod's Harmony patch is present
    /// on the same method (owner "NoSickness"), this guard stands down completely so ill days are
    /// not double-incremented — whichever is installed handles it, both installed is safe.
    /// </summary>
    internal static class IllnessDeathGuard
    {
        private static bool _enabled;
        private static MethodBase _target;
        private static bool _standDownChecked;
        private static bool _standDown;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _enabled = GuardConfig.Bool("noSickness", true);
                Type aging = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.AgingCampaignBehavior");
                _target = aging != null ? AccessTools.Method(aging, "DailyTickHero") : null;
                if (_target == null)
                {
                    Log.Info("[NOSICK] AgingCampaignBehavior.DailyTickHero not found — guard inactive (game update?)");
                    Diag.Report("illness-death-guard", false, "DailyTickHero not found");
                    return;
                }
                if (!_enabled)
                {
                    Log.Info("[NOSICK] disabled by config (noSickness: false)");
                    Diag.Report("illness-death-guard", true, "disabled by config");
                    return;
                }
                harmony.Patch(_target, new HarmonyMethod(typeof(IllnessDeathGuard), nameof(Prefix)));
                Log.Info("[NOSICK] illness-death guard active — the local player can no longer die of sickness");
                Diag.Report("illness-death-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[NOSICK] apply failed: " + ex.Message);
                Diag.Report("illness-death-guard", false, ex.Message);
            }
        }

        private static bool Prefix(Hero hero)
        {
            try
            {
                if (!_enabled || StandDown())
                {
                    return true;
                }
                Hero main = Hero.MainHero;
                if (hero == null || main == null || hero != main)
                {
                    return true; // only the local player's hero has the illness mechanic
                }
                if (!Hero.IsMainHeroIll || (int)main.HeroState == 5 /* Dead */)
                {
                    return true; // healthy (or already dead) — vanilla aging runs normally
                }
                Campaign campaign = Campaign.Current;
                if (campaign == null)
                {
                    return true;
                }
                // Take over the ill-day tick so vanilla's illness-death branch never runs.
                campaign.MainHeroIllDays++;
                if (campaign.MainHeroIllDays > 3 && main.HitPoints <= 1 && (int)main.DeathMark == 0)
                {
                    // This is the day vanilla would have rolled the death — cure the cycle instead.
                    campaign.MainHeroIllDays = -1;
                    SelfHealing.RecordFire("illness-death-guard");
                    Log.Info("[NOSICK] BLOCKED illness death of " + main.Name + " — illness cured");
                    Log.Screen("sickness would have killed you — blocked by the no-sickness guard");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("[NOSICK] prefix error, passing through to vanilla: " + ex.Message);
                return true;
            }
        }

        private static bool StandDown()
        {
            if (_standDownChecked)
            {
                return _standDown;
            }
            _standDownChecked = true; // checked lazily so a later-loading NoSickness module is still seen
            try
            {
                Patches info = Harmony.GetPatchInfo(_target);
                _standDown = info != null && info.Owners.Contains("NoSickness");
                if (_standDown)
                {
                    Log.Info("[NOSICK] third-party NoSickness mod patch detected on DailyTickHero — standing down (it handles the block; no double ill-day tick)");
                }
            }
            catch
            {
                _standDown = false;
            }
            return _standDown;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            // Prove the decision wiring: target resolved, and the prefix passes through (returns
            // true = run vanilla) for a null hero — the only input testable outside a campaign.
            bool methodExists = _target != null;
            bool passThroughOnNull = Prefix(null);
            bool pass = methodExists && passThroughOnNull;
            return SelfHealing.TestResult.Of("illness-death-guard.contract", pass,
                pass ? "target present; prefix passes through on null hero"
                     : "methodExists=" + methodExists + " passThroughOnNull=" + passThroughOnNull);
        }
    }
}
