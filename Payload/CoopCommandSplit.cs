using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// CO-OP: each player commands their OWN army — the host theirs, the client theirs.
    /// Field request 2026-09-03: "in co-op I should be able to command my own army while the
    /// host commands theirs."
    ///
    /// How BannerlordTogether already decides command (read from the installed build's IL):
    ///  - The host approves a formation for the client only when it holds the client's troops
    ///    ALONE (SpNativeBattleHostMissionBehavior.IsClientFormationCommandApproved:
    ///    FormationHasClientOwnedUnit && !FormationHasHostOwnedUnit, or the client is its
    ///    PlayerOwner/Captain). Approved formations become the client's AllowedFormationMask;
    ///    inside an army the client is a sergeant over exactly those, otherwise their general.
    ///  - The client sends the host a formation-membership snapshot of its own troops every
    ///    second (SendFormationMembershipSnapshot: host agent index + FormationClass) and the
    ///    host moves those agents into the named formation (ApplyClientFormationMembership →
    ///    ResolveFormationByClass) when the claim is allowed.
    ///  - Vanilla spawns BOTH parties' troops into the same class formations (Infantry, Ranged,
    ///    ...), so every formation is mixed, nothing is ever purely the client's, the mask is
    ///    empty and the client commands nothing ("[SPNATIVE ORDER-GUARD] blocked local ...").
    ///
    /// Fix (guardconfig `coopOwnArmyCommand`, default true): in a live co-op battle the two
    /// players' parties are kept in SEPARATE formation blocks on both machines —
    ///   host party (and every AI party on the side): I–IV = infantry / archers / cavalry / horse archers
    ///   client party:                                 V–VIII = the same four, in that order.
    /// Applied at spawn (Mission.SpawnTroop postfix), re-applied when deployment ends and every
    /// half second (the Order of Battle screen and reinforcements re-sort by class). With the
    /// blocks clean, BT's own rules do the rest: the client's snapshot names V–VIII, the host
    /// approves them, and each player orders their own block while BT forwards the client's
    /// orders to the host. Player heroes are never moved; companions travel with their party.
    /// Solo play is untouched (no remote peer → inert).
    /// </summary>
    internal static class CoopCommandSplit
    {
        private const string Tag = "[COOP-CMD]";
        private const string Component = "coop-command-split";
        private const int BlockSize = 4;
        private const int EnforceIntervalMs = 500;
        private const int ResolveRetryMs = 2000;

        private static bool _applied;
        private static int _lastEnforceTick;
        private static int _lastResolveTick;
        private static int _lastLogTick;
        private static int _movedThisBattle;
        private static bool _announced;
        private static PartyBase _clientParty;
        private static PartyBase _hostParty;
        private static string _ghostHeroId;
        private static string _clientName;
        private static string _hostName;

        internal static void Apply(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                if (!GuardConfig.Bool("coopOwnArmyCommand", true))
                {
                    Log.Info(Tag + " co-op own-army command DISABLED (guardconfig coopOwnArmyCommand=false)");
                    Diag.Report(Component, true, "disabled by config");
                    return;
                }
                int patched = 0;
                foreach (MethodInfo method in typeof(Mission).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "SpawnTroop" || method.ReturnType != typeof(Agent))
                    {
                        continue;
                    }
                    harmony.Patch(method, null, new HarmonyMethod(typeof(CoopCommandSplit), nameof(SpawnTroopPostfix)));
                    patched++;
                }
                MethodInfo deploymentFinished = AccessTools.Method(typeof(Mission), "OnDeploymentFinished");
                if (patched == 0 || deploymentFinished == null)
                {
                    Log.Info(Tag + " inactive — Mission.SpawnTroop / OnDeploymentFinished not resolved (game update?)");
                    Diag.Report(Component, false, "members not resolved");
                    return;
                }
                harmony.Patch(deploymentFinished, null, new HarmonyMethod(typeof(CoopCommandSplit), nameof(DeploymentFinishedPostfix)));
                _applied = true;
                Log.Info(Tag + " active — in a co-op battle the host's army fights in formations I–IV and the client's in V–VIII, so BannerlordTogether lets each player command their own troops (SpawnTroop overloads hooked: " + patched + ")");
                Diag.Report(Component, true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        internal static void OnMissionInit()
        {
            _lastEnforceTick = 0;
            _lastResolveTick = 0;
            _lastLogTick = 0;
            _movedThisBattle = 0;
            _announced = false;
            _clientParty = null;
            _hostParty = null;
            _ghostHeroId = null;
            _clientName = null;
            _hostName = null;
        }

        internal static void Tick()
        {
            if (!_applied)
            {
                return;
            }
            try
            {
                Mission mission = Mission.Current;
                if (mission == null || mission.PlayerTeam == null)
                {
                    return;
                }
                int now = Environment.TickCount;
                if (_lastEnforceTick != 0 && now - _lastEnforceTick < EnforceIntervalMs && now >= _lastEnforceTick)
                {
                    return;
                }
                _lastEnforceTick = now;
                Enforce(mission, "tick");
            }
            catch (Exception ex)
            {
                LogRateLimited(Tag + " tick error: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- decision core

        /// <summary>The four basic roles every troop class folds into: 0 infantry (incl.
        /// skirmishers, heavy infantry), 1 archers, 2 cavalry (incl. light/heavy), 3 horse archers.</summary>
        internal static int BasicSlot(FormationClass formationClass)
        {
            switch (formationClass)
            {
                case FormationClass.Ranged:
                    return 1;
                case FormationClass.Cavalry:
                case FormationClass.LightCavalry:
                case FormationClass.HeavyCavalry:
                    return 2;
                case FormationClass.HorseArcher:
                    return 3;
                default:
                    return 0;
            }
        }

        /// <summary>Regular formation index a troop belongs in: the host block (0–3) or the client block (4–7).</summary>
        internal static int TargetIndex(bool clientParty, FormationClass troopClass)
        {
            return BasicSlot(troopClass) + (clientParty ? BlockSize : 0);
        }

        /// <summary>True when a troop sits in the other player's block (only regular formations 0–7 count).</summary>
        internal static bool IsOutOfBlock(bool clientParty, int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= BlockSize * 2)
            {
                return false;
            }
            return clientParty ? currentIndex < BlockSize : currentIndex >= BlockSize;
        }

        // ---------------------------------------------------------------- patches

        private static void SpawnTroopPostfix(Agent __result)
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission == null || __result == null)
                {
                    return;
                }
                if (!ResolveParties(mission))
                {
                    return;
                }
                if (Place(__result, mission))
                {
                    _movedThisBattle++;
                    SelfHealing.RecordFire(Component);
                    Announce(mission);
                }
            }
            catch (Exception ex)
            {
                LogRateLimited(Tag + " spawn placement error: " + ex.Message);
            }
        }

        private static void DeploymentFinishedPostfix()
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission != null && mission.PlayerTeam != null)
                {
                    Enforce(mission, "deployment-finished");
                }
            }
            catch (Exception ex)
            {
                LogRateLimited(Tag + " deployment enforcement error: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- enforcement

        private static void Enforce(Mission mission, string reason)
        {
            if (!ResolveParties(mission))
            {
                return;
            }
            Team team = mission.PlayerTeam;
            List<Agent> units = new List<Agent>();
            foreach (Formation formation in team.FormationsIncludingEmpty)
            {
                if (formation.CountOfUnits == 0)
                {
                    continue;
                }
                formation.ApplyActionOnEachUnit(delegate (Agent agent) { units.Add(agent); });
            }
            int moved = 0;
            foreach (Agent agent in units)
            {
                if (Place(agent, mission))
                {
                    moved++;
                }
            }
            if (moved > 0)
            {
                _movedThisBattle += moved;
                SelfHealing.RecordFire(Component);
                Announce(mission);
                LogRateLimited(Tag + " re-sorted " + moved + " troop(s) into their owner's block (" + reason + ") — " + _movedThisBattle + " this battle");
            }
        }

        /// <summary>Move one agent into its owner's block if it sits in the other player's. Player
        /// heroes (either machine's) are never moved; companions travel with their party.</summary>
        private static bool Place(Agent agent, Mission mission)
        {
            if (agent == null || !agent.IsHuman || agent.Team == null || agent.Team != mission.PlayerTeam || agent.Formation == null)
            {
                return false;
            }
            if (agent.IsPlayerControlled || IsPlayerHeroAgent(agent))
            {
                return false;
            }
            PartyAgentOrigin origin = agent.Origin as PartyAgentOrigin;
            PartyBase party = origin != null ? origin.Party : null;
            if (party == null)
            {
                return false;
            }
            bool clientParty = party == _clientParty;
            int current = (int)agent.Formation.FormationIndex;
            if (!IsOutOfBlock(clientParty, current))
            {
                return false;
            }
            FormationClass troopClass = agent.Character != null ? agent.Character.DefaultFormationClass : agent.Formation.FormationIndex;
            Formation target = agent.Team.GetFormation((FormationClass)TargetIndex(clientParty, troopClass));
            if (target == null || target == agent.Formation)
            {
                return false;
            }
            agent.Formation = target;
            return true;
        }

        private static bool IsPlayerHeroAgent(Agent agent)
        {
            try
            {
                if (agent == Agent.Main)
                {
                    return true;
                }
                CharacterObject character = agent.Character as CharacterObject;
                Hero hero = character != null ? character.HeroObject : null;
                if (hero == null)
                {
                    return false;
                }
                if (hero == Hero.MainHero)
                {
                    return true;
                }
                return _ghostHeroId != null && (hero.StringId == _ghostHeroId || character.StringId == _ghostHeroId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Which party is the CLIENT's and which the HOST's, on this machine. Needs a live
        /// BT session with a remote player; the remote player's hero is the session's "ghost hero".</summary>
        private static bool ResolveParties(Mission mission)
        {
            if (_clientParty != null && _hostParty != null)
            {
                return true;
            }
            int now = Environment.TickCount;
            if (_lastResolveTick != 0 && now - _lastResolveTick < ResolveRetryMs && now >= _lastResolveTick)
            {
                return false;
            }
            _lastResolveTick = now;
            try
            {
                bool isClient = PeerDetection.IsClient() == true;
                bool isHost = !isClient && PeerDetection.AnyRemotePeerConnected() == true;
                if (!isClient && !isHost)
                {
                    return false; // solo — nothing to split
                }
                if (Campaign.Current == null || PartyBase.MainParty == null)
                {
                    return false;
                }
                string ghostId = PeerDetection.ReadCoopStaticString("GhostHeroStringId");
                if (string.IsNullOrEmpty(ghostId))
                {
                    return false;
                }
                Hero ghost = MBObjectManager.Instance.GetObject<Hero>(ghostId);
                if (ghost == null)
                {
                    CharacterObject character = MBObjectManager.Instance.GetObject<CharacterObject>(ghostId);
                    ghost = character != null ? character.HeroObject : null;
                }
                MobileParty ghostParty = ghost != null ? ghost.PartyBelongedTo : null;
                if (ghostParty == null || ghostParty.Party == null || ghostParty.Party == PartyBase.MainParty)
                {
                    return false;
                }
                _ghostHeroId = ghostId;
                _clientParty = isClient ? PartyBase.MainParty : ghostParty.Party;
                _hostParty = isClient ? ghostParty.Party : PartyBase.MainParty;
                _clientName = isClient ? HeroName(Hero.MainHero) : HeroName(ghost);
                _hostName = isClient ? HeroName(ghost) : HeroName(Hero.MainHero);
                return true;
            }
            catch (Exception ex)
            {
                LogRateLimited(Tag + " could not resolve the two parties: " + ex.Message);
                return false;
            }
        }

        private static void Announce(Mission mission)
        {
            if (_announced)
            {
                return;
            }
            _announced = true;
            Log.Info(Tag + " co-op battle: " + _hostName + "'s army fights in formations I–IV (infantry / archers / cavalry / horse archers), " +
                     _clientName + "'s in V–VIII — each player commands their own block; BannerlordTogether approves a formation for a player only when it holds that player's troops alone");
            Log.Screen("Co-op: " + _hostName + " commands I–IV, " + _clientName + " commands V–VIII (own army each)");
        }

        private static string HeroName(Hero hero)
        {
            try
            {
                return hero != null && hero.Name != null ? hero.Name.ToString() : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void LogRateLimited(string message)
        {
            int now = Environment.TickCount;
            if (_lastLogTick != 0 && now - _lastLogTick < 5000 && now >= _lastLogTick)
            {
                return;
            }
            _lastLogTick = now;
            Log.Info(message);
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = AccessTools.Method(typeof(Mission), "OnDeploymentFinished") != null &&
                            AccessTools.Method(typeof(Team), "GetFormation", new[] { typeof(FormationClass) }) != null &&
                            AccessTools.Property(typeof(Agent), "Formation") != null &&
                            AccessTools.Property(typeof(PartyAgentOrigin), "Party") != null;
            int spawnOverloads = 0;
            foreach (MethodInfo method in typeof(Mission).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.Name == "SpawnTroop" && method.ReturnType == typeof(Agent))
                {
                    spawnOverloads++;
                }
            }
            bool blocks =
                TargetIndex(false, FormationClass.Infantry) == 0 && TargetIndex(false, FormationClass.Ranged) == 1 &&
                TargetIndex(false, FormationClass.Cavalry) == 2 && TargetIndex(false, FormationClass.HorseArcher) == 3 &&
                TargetIndex(false, FormationClass.Skirmisher) == 0 && TargetIndex(false, FormationClass.HeavyInfantry) == 0 &&
                TargetIndex(false, FormationClass.LightCavalry) == 2 && TargetIndex(false, FormationClass.HeavyCavalry) == 2 &&
                TargetIndex(true, FormationClass.Infantry) == 4 && TargetIndex(true, FormationClass.Ranged) == 5 &&
                TargetIndex(true, FormationClass.Cavalry) == 6 && TargetIndex(true, FormationClass.HorseArcher) == 7 &&
                IsOutOfBlock(true, 0) && IsOutOfBlock(true, 3) && !IsOutOfBlock(true, 4) && !IsOutOfBlock(true, 7) &&
                IsOutOfBlock(false, 4) && IsOutOfBlock(false, 7) && !IsOutOfBlock(false, 0) && !IsOutOfBlock(false, 3) &&
                !IsOutOfBlock(true, 8) && !IsOutOfBlock(false, 9) && !IsOutOfBlock(false, -1);
            bool pass = resolved && spawnOverloads > 0 && blocks;
            return SelfHealing.TestResult.Of("coop-command-split.contract", pass,
                pass ? "members re-resolved (SpawnTroop overloads " + spawnOverloads + "); block mapping verified"
                     : "resolved=" + resolved + " spawnOverloads=" + spawnOverloads + " blocks=" + blocks);
        }
    }
}
