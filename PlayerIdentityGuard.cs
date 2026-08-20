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
        private const int MaxCorrectionsPerMission = 5;

        private static Mission _lastMission;
        private static int _corrections;
        private static int _lastCheckTick;

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
                if (_corrections >= MaxCorrectionsPerMission)
                {
                    return;
                }
                if (mission.GetMissionBehavior<DeploymentMissionController>() != null)
                {
                    return; // deployment phase: Controller=None on the player agent is legitimate
                }
                Hero mainHero = Hero.MainHero;
                if (mainHero == null || mainHero.CharacterObject == null)
                {
                    return;
                }
                BasicCharacterObject myCharacter = mainHero.CharacterObject;
                Agent controlled = mission.MainAgent;
                if (controlled != null && ReferenceEquals(controlled.Character, myCharacter))
                {
                    return; // identity correct
                }

                Agent myAgent = null;
                foreach (Agent agent in mission.Agents)
                {
                    if (agent != null && agent.IsHuman && ReferenceEquals(agent.Character, myCharacter) && agent.IsActive())
                    {
                        myAgent = agent;
                        break;
                    }
                }
                if (myAgent == null || ReferenceEquals(myAgent, controlled))
                {
                    return; // our hero isn't in this mission (spectating etc.) — nothing safe to do
                }

                _corrections++;
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
