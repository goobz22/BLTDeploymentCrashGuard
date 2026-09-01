using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// The hideout "Sneak in" (field report 2026-09-01: "spawned me into a main camp not as
    /// myself but as a soldier and I cannot command my army"). Decoded from the installed
    /// build's IL — SandBox.Missions.MissionLogics.Hideout.HideoutAmbushMissionController:
    ///  - AfterStart spawns YOUR hero, then re-dresses it in Hero.StealthEquipment with the
    ///    enemy's clothing colors (UpdateSpawnEquipmentAndRefreshVisuals) — the "soldier"
    ///    look is your disguise, not a wrong agent (the control trace confirms MainAgent is
    ///    your hero);
    ///  - the mission starts in STEALTH mode with a "locate the main camp" objective; your
    ///    troops are held back and orders are withheld by design; being spotted too long
    ///    fails the counter and ends the mission ("found by sentries");
    ///  - ChangeHideoutMissionModeToBattle / the boss-fight battle mode are where the
    ///    player order controller is selected and the army arrives — orders work from there.
    /// So this is vanilla design, not a bug. What this class adds:
    ///  1. an on-screen explainer the moment a sneak-in starts, so nobody thinks the game
    ///     broke;
    ///  2. a guarantee at every stealth->battle transition that the LOCAL player owns the
    ///     team's order controller and is its general (repairs otherwise) — vanilla merely
    ///     assumes it, and in co-op BT's battle patches make that assumption fragile.
    /// All by-name reflection (SandBox is not a compile-time reference).
    /// </summary>
    internal static class StealthHideoutAdvisor
    {
        private const string ControllerType = "SandBox.Missions.MissionLogics.Hideout.HideoutAmbushMissionController";

        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type controller = AccessTools.TypeByName(ControllerType);
                if (controller == null)
                {
                    return; // older game build without the stealth hideout — nothing to advise
                }
                int patched = 0;
                MethodInfo afterStart = AccessTools.Method(controller, "AfterStart");
                if (afterStart != null)
                {
                    harmony.Patch(afterStart, null, new HarmonyMethod(typeof(StealthHideoutAdvisor), nameof(AfterStartPostfix)));
                    patched++;
                }
                foreach (string transition in new[] { "ChangeHideoutMissionModeToBattle", "StartBossFightBattleModeInternal", "StartBossFightDuelModeInternal" })
                {
                    MethodInfo m = AccessTools.Method(controller, transition);
                    if (m != null)
                    {
                        harmony.Patch(m, null, new HarmonyMethod(typeof(StealthHideoutAdvisor), nameof(BattleTransitionPostfix)));
                        patched++;
                    }
                }
                Log.Info("[STEALTH] hideout sneak-in advisor active on " + patched + " method(s)");
                Diag.Report("stealth-hideout-advisor", patched > 0, patched > 0 ? "" : "no methods resolved");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[STEALTH] apply failed: " + ex.Message);
                Diag.Report("stealth-hideout-advisor", false, ex.Message);
            }
        }

        private static void AfterStartPostfix()
        {
            try
            {
                Log.Info("[STEALTH] sneak-in started: player re-dressed in stealth equipment (enemy colors); stealth phase — orders withheld until the ambush is sprung");
                Log.Screen("SNEAK-IN: you are disguised in your stealth outfit — find the main camp to spring the ambush; your troops and orders arrive when the fight starts");
            }
            catch
            {
            }
        }

        private static void BattleTransitionPostfix()
        {
            try
            {
                Mission mission = Mission.Current;
                Agent main = Agent.Main;
                Team team = mission != null ? mission.PlayerTeam : null;
                if (team == null || main == null || !main.IsActive())
                {
                    return;
                }
                int repaired = 0;
                if (team.GeneralAgent == null || !ReferenceEquals(team.GeneralAgent, main))
                {
                    team.GeneralAgent = main;
                    repaired++;
                }
                OrderController orders = team.PlayerOrderController;
                if (orders != null && !ReferenceEquals(orders.Owner, main))
                {
                    orders.Owner = main;
                    repaired++;
                }
                if (repaired > 0)
                {
                    SelfHealing.RecordFire("stealth-hideout-advisor");
                    Log.Info("[STEALTH] ambush sprung — repaired " + repaired + " command link(s) so the player commands the squad (general/order-controller owner)");
                }
                else
                {
                    Log.Info("[STEALTH] ambush sprung — player already general + order-controller owner; orders available");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[STEALTH] transition check failed: " + ex.Message);
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type controller = AccessTools.TypeByName(ControllerType);
            bool resolved = controller != null &&
                            AccessTools.Method(controller, "AfterStart") != null &&
                            AccessTools.Method(controller, "ChangeHideoutMissionModeToBattle") != null;
            return SelfHealing.TestResult.Of("stealth-hideout-advisor.contract", resolved,
                resolved ? "ambush controller + transitions re-resolved" : "controller/transitions not resolved (game update?)");
        }
    }
}
