using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Fixes the co-op identity swap (2026-08-19 20:11, control map in CrashGuard.log):
    /// with two player heroes in one mission, the spawn sync sometimes builds the
    /// OTHER player's hero as the local player agent — it becomes MainAgent /
    /// InitialPlayerAgent, team general, order-controller owner and formation owner,
    /// while the local hero spawns AI-controlled ("I spawned as an AI and an AI
    /// spawned as me").
    ///
    /// Corrective check, once per second during campaign missions (skipped while the
    /// deployment phase is active, where Controller=None is legitimate): if the
    /// controlled agent's character is not Hero.MainHero, and the local hero's agent
    /// exists in the mission, move player control to the local hero and hand the
    /// impostor back to AI, then repair general/order-controller/formation ownership.
    /// Capped at 5 corrections per mission so it can never fight another system in a
    /// loop.
    /// </summary>
    internal static class PlayerIdentityGuard
    {
        private const string Component = "player-identity-guard";
        private const string Tag = "[IDENTITY]";
        private const int MaxCorrectionsPerMission = 5;

        private static Mission _lastMission;
        private static int _corrections;
        private static int _lastCheckTick;
        private static bool _testRegistered;

        /// <summary>Tick-driven, so Apply only pins the mission/agent members the corrector writes
        /// (compile-bound, but a game update renaming one would otherwise surface as a caught
        /// MissingMethodException every second) and registers the self-test (added 2026-09-04 —
        /// this guard used to be invisible to MOD HEALTH).</summary>
        internal static void Apply()
        {
            try
            {
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                string missing = MissingMembers();
                Diag.Report(Component, missing.Length == 0, missing.Length == 0 ? "" : "missing " + missing + " (game update?)");
                if (missing.Length > 0)
                {
                    Log.Info(Tag + " member(s) missing — corrector inactive: " + missing + " (game update?)");
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        /// <summary>Every engine member the correction writes or reads, checked by name.</summary>
        private static string MissingMembers()
        {
            string missing = "";
            if (AccessTools.Property(typeof(Mission), "MainAgent") == null) missing += " Mission.MainAgent";
            if (AccessTools.PropertySetter(typeof(Agent), "Controller") == null) missing += " Agent.Controller";
            if (AccessTools.Property(typeof(Agent), "Character") == null) missing += " Agent.Character";
            if (AccessTools.Property(typeof(Team), "GeneralAgent") == null) missing += " Team.GeneralAgent";
            if (AccessTools.Property(typeof(Team), "PlayerOrderController") == null) missing += " Team.PlayerOrderController";
            if (AccessTools.Property(typeof(OrderController), "Owner") == null) missing += " OrderController.Owner";
            if (AccessTools.Property(typeof(Formation), "PlayerOwner") == null) missing += " Formation.PlayerOwner";
            if (AccessTools.Property(typeof(Hero), "MainHero") == null) missing += " Hero.MainHero";
            return missing.Trim();
        }

        /// <summary>The whole decision, engine-free so the self-test can pin it: correct only outside
        /// the deployment phase (Controller=None is legitimate there), when the controlled agent is
        /// not our hero, our hero's agent is present and is not already the controlled one, and the
        /// per-mission cap has not been reached.</summary>
        internal static bool NeedsCorrection(bool inDeployment, bool controlledIsMine, bool myAgentPresent, bool myAgentIsControlled, int corrections)
        {
            return !inDeployment && !controlledIsMine && myAgentPresent && !myAgentIsControlled && corrections < MaxCorrectionsPerMission;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            string missing = MissingMembers();
            bool members = missing.Length == 0;
            bool decisions =
                NeedsCorrection(false, false, true, false, 0) &&
                NeedsCorrection(false, false, true, false, MaxCorrectionsPerMission - 1) &&
                !NeedsCorrection(true, false, true, false, 0) &&     // deployment phase
                !NeedsCorrection(false, true, true, true, 0) &&      // identity already correct
                !NeedsCorrection(false, false, false, false, 0) &&   // our hero is not in this mission
                !NeedsCorrection(false, false, true, true, 0) &&     // our agent is the controlled one after all
                !NeedsCorrection(false, false, true, false, MaxCorrectionsPerMission); // cap reached
            bool pass = members && decisions;
            return SelfHealing.TestResult.Of(Component + ".contract", pass,
                pass ? "Mission/Agent/Team/OrderController/Formation/Hero members and the correction decision table verified"
                     : "members=" + members + (members ? "" : " (missing " + missing + ")") + " decisions=" + decisions);
        }

        private static Agent FindMyAgent(Mission mission, BasicCharacterObject myCharacter)
        {
            foreach (Agent agent in mission.Agents)
            {
                if (agent != null && agent.IsHuman && ReferenceEquals(agent.Character, myCharacter) && agent.IsActive())
                {
                    return agent;
                }
            }
            return null;
        }

        internal static void Tick()
        {
            try
            {
                int now = Environment.TickCount;
                if (_lastCheckTick != 0 && now - _lastCheckTick < 1000 && now >= _lastCheckTick)
                {
                    return;
                }
                _lastCheckTick = now;

                Mission mission = Mission.Current;
                if (mission == null || Campaign.Current == null || mission.Scene == null)
                {
                    return;
                }
                if (!ReferenceEquals(mission, _lastMission))
                {
                    _lastMission = mission;
                    _corrections = 0;
                }
                Hero mainHero = Hero.MainHero;
                if (mainHero == null || mainHero.CharacterObject == null)
                {
                    return;
                }
                BasicCharacterObject myCharacter = mainHero.CharacterObject;
                Agent controlled = mission.MainAgent;
                bool controlledIsMine = controlled != null && ReferenceEquals(controlled.Character, myCharacter);
                if (controlledIsMine)
                {
                    return; // identity correct — the common case, settled before any agent scan
                }
                bool inDeployment = mission.GetMissionBehavior<DeploymentMissionController>() != null;
                Agent myAgent = FindMyAgent(mission, myCharacter);
                if (!NeedsCorrection(inDeployment, controlledIsMine, myAgent != null, myAgent != null && ReferenceEquals(myAgent, controlled), _corrections))
                {
                    // deployment phase (Controller=None on the player agent is legitimate there), our
                    // hero is not in this mission (spectating etc.), or the per-mission cap is reached
                    return;
                }

                _corrections++;
                SelfHealing.RecordFire(Component); // feeds GUARD ACTIVITY so the fix is retirable when BT fixes the swap
                Log.Info("[IDENTITY] player control is on the wrong agent (" + Describe(controlled) + ") — moving control to " + Describe(myAgent) + " (correction " + _corrections + "/" + MaxCorrectionsPerMission + ")");

                try
                {
                    if (controlled != null && controlled.IsActive())
                    {
                        controlled.Controller = AgentControllerType.AI;
                    }
                }
                catch (Exception exImpostor)
                {
                    Log.Info("[IDENTITY] impostor handback failed: " + exImpostor.Message);
                }

                myAgent.Controller = AgentControllerType.Player;

                Team team = myAgent.Team ?? mission.PlayerTeam;
                if (team != null)
                {
                    try
                    {
                        if (team.GeneralAgent == null || ReferenceEquals(team.GeneralAgent, controlled))
                        {
                            team.GeneralAgent = myAgent;
                        }
                    }
                    catch { }
                    try
                    {
                        OrderController orders = team.PlayerOrderController;
                        if (orders != null && (orders.Owner == null || ReferenceEquals(orders.Owner, controlled)))
                        {
                            orders.Owner = myAgent;
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (Formation formation in team.FormationsIncludingSpecialAndEmpty)
                        {
                            if (formation != null && ReferenceEquals(formation.PlayerOwner, controlled))
                            {
                                formation.PlayerOwner = myAgent;
                            }
                        }
                    }
                    catch { }
                }

                Log.Screen("fixed player identity — you are back in control of your own character");
            }
            catch (Exception ex)
            {
                Log.Info("[IDENTITY] check failed: " + ex.Message);
            }
        }

        private static string Describe(Agent agent)
        {
            if (agent == null)
            {
                return "null";
            }
            try
            {
                string name = agent.Character != null && agent.Character.Name != null ? agent.Character.Name.ToString() : ("agent#" + agent.Index);
                return name + (agent.Team != null ? "(" + agent.Team.Side + ")" : "");
            }
            catch
            {
                return "agent(?)";
            }
        }
    }
}
