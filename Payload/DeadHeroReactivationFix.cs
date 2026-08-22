using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// ROOT FIX for the issue-quest CTD (field crash 2026-08-21 22:25). Decompile-proven chain:
    /// clicking OK on an issue popup ran IssueManager.MakeAlternativeTroopsReturn, which loops the
    /// troops you sent as an alternative solution and calls Hero.ChangeState(Active) on every hero
    /// among them — WITHOUT checking IsAlive. If one of those companions DIED while away, the game
    /// reactivates a corpse: ChangeState(Active) fires OnHeroActivatedEvent ->
    /// CharacterDevelopmentCampaignBehavior.OnHeroActivated -> hero.HeroDeveloper.DevelopCharacterStats().
    /// Hero.OnDeath() nulls _heroDeveloper (it is the ONLY place it is nulled — proven), so a dead
    /// hero has no developer and that dereference NREs, taking the click to desktop.
    ///
    /// This is NOT fixed by guarding the perk code — the defect is that a DEAD hero is being
    /// reactivated and re-added to the party at all. Two root-level fixes:
    ///
    ///  1. CALLER FIX — prefix MakeAlternativeTroopsReturn: strip dead heroes from the returning
    ///     roster before the original runs. Dead companions simply do not return (correct — they
    ///     are dead); living troops return exactly as before. Fixes the buggy data flow AND stops
    ///     a dead hero being added to your party roster.
    ///  2. INVARIANT FIX (the CLASS, per T8) — prefix Hero.ChangeState: a dead hero can never
    ///     transition to Active. This is a domain invariant true for EVERY caller, so any other
    ///     code path that erroneously reactivates a dead hero is also protected. (A legitimate
    ///     revive clears the dead state first, so IsDead is already false and this never blocks it.)
    ///
    /// Both self-disable in effect: once TaleWorlds stops feeding dead heroes into these paths the
    /// prefixes simply never intervene (visible as never-fired in the health report).
    /// </summary>
    internal static class DeadHeroReactivationFix
    {
        internal static void Apply(Harmony harmony)
        {
            ApplyCallerFix(harmony);
            ApplyInvariantFix(harmony);
        }

        // ---- fix 1: the buggy caller ------------------------------------------------------

        private static void ApplyCallerFix(Harmony harmony)
        {
            try
            {
                Type issueManager = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Issues.IssueManager");
                var method = issueManager != null
                    ? AccessTools.Method(issueManager, "MakeAlternativeTroopsReturn", new[] { typeof(TroopRoster) })
                    : null;
                if (method == null)
                {
                    Log.Info("[DEADHERO] IssueManager.MakeAlternativeTroopsReturn(TroopRoster) not found — caller fix inactive (game update?)");
                    Diag.Report("dead-hero-return-fix", false, "MakeAlternativeTroopsReturn not found");
                    return;
                }
                harmony.Patch(method, new HarmonyMethod(typeof(DeadHeroReactivationFix), nameof(MakeAlternativeTroopsReturnPrefix)));
                Log.Info("[DEADHERO] returning-troops fix active — dead companions are removed before an issue returns its troops");
                Diag.Report("dead-hero-return-fix", true, "");
                SelfHealing.RegisterTest(CallerFixSelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[DEADHERO] caller fix apply failed: " + ex.Message);
                Diag.Report("dead-hero-return-fix", false, ex.Message);
            }
        }

        private static void MakeAlternativeTroopsReturnPrefix(TroopRoster roster)
        {
            try
            {
                if (roster == null)
                {
                    return;
                }
                var removed = roster.RemoveIf(IsDeadHeroElement);
                if (removed != null && removed.Count > 0)
                {
                    SelfHealing.RecordFire("dead-hero-return-fix");
                    Log.Info("[DEADHERO] removed " + removed.Count + " dead hero(es) from a returning-troops roster before reactivation");
                    Log.Screen("a companion who died while away could not return");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[DEADHERO] roster clean error (letting original run): " + ex.Message);
            }
        }

        private static bool IsDeadHeroElement(TroopRosterElement element)
        {
            try
            {
                return element.Character != null
                    && element.Character.IsHero
                    && element.Character.HeroObject != null
                    && !element.Character.HeroObject.IsAlive;
            }
            catch
            {
                return false; // never remove on uncertainty
            }
        }

        // ---- fix 2: the domain invariant (the whole class) --------------------------------

        private static void ApplyInvariantFix(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(Hero), "ChangeState");
                if (method == null)
                {
                    Log.Info("[DEADHERO] Hero.ChangeState not found — invariant fix inactive (game update?)");
                    Diag.Report("dead-hero-activate-invariant", false, "Hero.ChangeState not found");
                    return;
                }
                harmony.Patch(method, new HarmonyMethod(typeof(DeadHeroReactivationFix), nameof(ChangeStatePrefix)));
                Log.Info("[DEADHERO] dead->Active invariant active — a dead hero can never be reactivated (protects every caller)");
                Diag.Report("dead-hero-activate-invariant", true, "");
                SelfHealing.RegisterTest(InvariantSelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[DEADHERO] invariant fix apply failed: " + ex.Message);
                Diag.Report("dead-hero-activate-invariant", false, ex.Message);
            }
        }

        private static bool ChangeStatePrefix(Hero __instance, Hero.CharacterStates newState)
        {
            try
            {
                if (__instance != null && newState == Hero.CharacterStates.Active && __instance.IsDead)
                {
                    SelfHealing.RecordFire("dead-hero-activate-invariant");
                    Log.Info("[DEADHERO] blocked reactivation of dead hero " + Describe(__instance) + " (dead->Active is never valid)");
                    return false; // skip: leave the hero Dead, fire no activation event
                }
            }
            catch (Exception ex)
            {
                Log.Info("[DEADHERO] invariant prefix error (letting original run): " + ex.Message);
            }
            return true;
        }

        private static string Describe(Hero hero)
        {
            try { return hero.Name != null ? hero.Name.ToString() : hero.StringId; }
            catch { return "hero(?)"; }
        }

        // ---- self-tests --------------------------------------------------------------------

        private static SelfHealing.TestResult CallerFixSelfTest()
        {
            Type issueManager = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Issues.IssueManager");
            bool methodExists = issueManager != null
                && AccessTools.Method(issueManager, "MakeAlternativeTroopsReturn", new[] { typeof(TroopRoster) }) != null;
            // Predicate must not throw and must be false on an empty element (no character).
            bool safeOnEmpty = !IsDeadHeroElement(default(TroopRosterElement));
            bool pass = methodExists && safeOnEmpty;
            return SelfHealing.TestResult.Of("dead-hero-return-fix.contract", pass,
                pass ? "target re-resolved; dead-hero predicate safe on empty element"
                     : "methodExists=" + methodExists + " safeOnEmpty=" + safeOnEmpty + " (game update?)");
        }

        private static SelfHealing.TestResult InvariantSelfTest()
        {
            bool methodExists = AccessTools.Method(typeof(Hero), "ChangeState") != null;
            // With a null instance the prefix must let the original run (return true), never throw.
            bool passthroughOnNull = ChangeStatePrefix(null, Hero.CharacterStates.Active);
            bool pass = methodExists && passthroughOnNull;
            return SelfHealing.TestResult.Of("dead-hero-activate-invariant.contract", pass,
                pass ? "Hero.ChangeState re-resolved; prefix passes through on null instance"
                     : "methodExists=" + methodExists + " passthroughOnNull=" + passthroughOnNull);
        }
    }
}
