using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// SIEGE DEFENSE: you command every formation, and the formations you place stay where
    /// you put them. Field report 2026-09-03 (solo host, defending own castle): "my party
    /// runs off to guard the castle instead of staying where I set them down; when the
    /// castle is compromised they leave and it only gets them killed."
    ///
    /// Root cause, proven from the installed build's IL (never guessed):
    ///  - BattleDeploymentHandler.SetDefaultFormationOrders ends with
    ///    `SetOrder(IsSiegeBattle || IsSallyOutBattle ? AIControlOn : AIControlOff)` — in a
    ///    siege the DEFAULT for every formation is AI CONTROL ON. It runs from the player
    ///    side's auto-deploy (MissionOrderDeploymentControllerVM.DeployFormationsOfPlayer →
    ///    SiegeDeploymentHandler.AutoDeployTeamUsingTeamAI) and from the Auto-deploy button.
    ///  - An AI-controlled formation belongs to TacticDefendCastle: FormationAI.TickOccasionally
    ///    runs behaviors only while IsAIControlled, and the tactic assigns lanes and key
    ///    positions (walls, gate, keep) and re-balances troops between formations through
    ///    Formation.TransferUnits / Formation.Split. It re-plans on a wall breach — "retreat to
    ///    keep", "defend key position" — exactly the moment the player's troops abandon their spot.
    ///  - OrderController.BeforeSetOrder gives a formation back to the player ONLY when it is
    ///    AI-controlled AND has a PlayerOwner; Formation.RemoveUnit hands an emptied formation
    ///    back to the AI, so a wiped-and-refilled formation (reinforcements) is the AI's again.
    ///  - Team.SetPlayerRole hands EVERY formation to the AI when the player is not the general;
    ///    MapEvent.IsPlayerSergeant demotes the player only when they sit inside an army led by
    ///    someone else — even inside their own castle.
    ///
    /// Fix (guardconfig `siegeCommandAll`, default true) — siege battles where the player's
    /// team DEFENDS, the player is the general, regular formations (Infantry..HeavyCavalry):
    ///  1. Team.SetPlayerRole + AssignPlayerRoleInTeamMissionController: when the defended
    ///     settlement belongs to the player's clan, the player is the general, never a sergeant.
    ///  2. Mission.OnDeploymentFinished postfix: every formation still AI-controlled when
    ///     deployment ends is handed to the player with a MOVE order to where it stands.
    ///  3. Formation.SetControlledByAI prefix: after deployment no hand-off to the AI, except the
    ///     player's own F6 "delegate command" (OrderController.SetOrder(AIControlOn)), vanilla's
    ///     death hand-off (Team.DelegateCommandToAI) and BannerlordTogether's player-down
    ///     releases on the host.
    ///  4. Formation.TransferUnits prefix (the tactic-only API — the order UI uses
    ///     OrderController.TransferUnits): the tactic never pulls troops out of, or pushes troops
    ///     into, a formation the player commands.
    /// Deployment itself is untouched: vanilla's auto-deploy still positions formations first.
    /// Co-op: solo and BT-host machines run the guard; a BT client stands down (the host's
    /// command assignment is authoritative there — host the session to command your castle).
    /// </summary>
    internal static class SiegeCommandGuard
    {
        private const string Tag = "[SIEGE-CMD]";
        private const string Component = "siege-command-guard";
        private const int RegularFormationCount = (int)FormationClass.NumberOfRegularFormations;

        /// <summary>OrderController.SetOrder(AIControlOn) in flight — the player's own F6.</summary>
        [ThreadStatic] private static int _explicitAiDepth;
        /// <summary>Team.DelegateCommandToAI in flight — vanilla's hand-off when the player falls.</summary>
        [ThreadStatic] private static int _delegateDepth;
        /// <summary>A BannerlordTogether host "release formations to AI" in flight (a player went down).</summary>
        [ThreadStatic] private static int _btReleaseDepth;

        private static bool _applied;
        private static bool _btRetried;
        private static int _btPatched;
        private static bool _clientNoteLogged;
        private static bool _screenNoteShown;
        private static int _blockedHandoffs;
        private static int _blockedTransfers;
        private static int _lastBlockLogTick;
        private static FieldInfo _generalField;
        private static FieldInfo _sergeantField;

        internal static void Apply(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                if (!GuardConfig.Bool("siegeCommandAll", true))
                {
                    Log.Info(Tag + " siege command guard DISABLED (guardconfig siegeCommandAll=false) — vanilla's siege AI hand-off applies");
                    Diag.Report(Component, true, "disabled by config");
                    return;
                }
                MethodInfo setControlled = AccessTools.Method(typeof(Formation), "SetControlledByAI", new[] { typeof(bool), typeof(bool) });
                MethodInfo transfer = AccessTools.Method(typeof(Formation), "TransferUnits", new[] { typeof(Formation), typeof(int) });
                MethodInfo setRole = AccessTools.Method(typeof(Team), "SetPlayerRole", new[] { typeof(bool), typeof(bool) });
                MethodInfo delegateAi = AccessTools.Method(typeof(Team), "DelegateCommandToAI");
                MethodInfo setOrder = AccessTools.Method(typeof(OrderController), "SetOrder", new[] { typeof(OrderType) });
                MethodInfo deploymentFinished = AccessTools.Method(typeof(Mission), "OnDeploymentFinished");
                Type roleController = AccessTools.TypeByName("TaleWorlds.MountAndBlade.AssignPlayerRoleInTeamMissionController");
                MethodInfo roleAfterStart = roleController != null ? AccessTools.Method(roleController, "AfterStart") : null;
                _generalField = roleController != null ? AccessTools.Field(roleController, "<IsPlayerGeneral>k__BackingField") : null;
                _sergeantField = roleController != null ? AccessTools.Field(roleController, "<IsPlayerSergeant>k__BackingField") : null;

                if (setControlled == null || transfer == null || setRole == null || delegateAi == null || setOrder == null || deploymentFinished == null)
                {
                    Log.Info(Tag + " inactive — vanilla members not resolved (game update?)");
                    Diag.Report(Component, false, "members not resolved");
                    return;
                }
                harmony.Patch(setControlled, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(SetControlledByAIPrefix)));
                harmony.Patch(transfer, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(TransferUnitsPrefix)));
                harmony.Patch(setRole, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(SetPlayerRolePrefix)));
                harmony.Patch(delegateAi, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(DelegatePrefix)), null, null,
                    new HarmonyMethod(typeof(SiegeCommandGuard), nameof(DelegateFinalizer)));
                harmony.Patch(setOrder, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(SetOrderPrefix)), null, null,
                    new HarmonyMethod(typeof(SiegeCommandGuard), nameof(SetOrderFinalizer)));
                harmony.Patch(deploymentFinished, null, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(DeploymentFinishedPostfix)));
                bool roleHooked = roleAfterStart != null && _generalField != null && _sergeantField != null;
                if (roleHooked)
                {
                    harmony.Patch(roleAfterStart, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(RoleControllerAfterStartPrefix)));
                }
                else
                {
                    Log.Info(Tag + " role controller members not resolved — owner-is-general promotion limited to Team.SetPlayerRole");
                }
                _btPatched = PatchBtReleases(harmony);
                _applied = true;
                Log.Info(Tag + " active — in a siege defense you command every formation and placed formations hold " +
                         "(vanilla's siege default is AI control ON; the castle-defence tactic marches AI formations to the walls). " +
                         "BT host player-down releases hooked: " + _btPatched);
                Diag.Report(Component, true, roleHooked ? "" : "role controller unresolved");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        /// <summary>BannerlordTogether's assembly can load after us — retry its hooks once.</summary>
        internal static void RetryBt(Harmony harmony)
        {
            if (!_applied || _btPatched > 0 || _btRetried)
            {
                return;
            }
            _btRetried = true;
            _btPatched = PatchBtReleases(harmony);
            if (_btPatched > 0)
            {
                Log.Info(Tag + " BT host player-down releases hooked late: " + _btPatched);
            }
        }

        internal static void OnMissionInit()
        {
            _clientNoteLogged = false;
            _screenNoteShown = false;
            _blockedHandoffs = 0;
            _blockedTransfers = 0;
            _explicitAiDepth = 0;
            _delegateDepth = 0;
            _btReleaseDepth = 0;
        }

        private static int PatchBtReleases(Harmony harmony)
        {
            int count = 0;
            try
            {
                Type host = AccessTools.TypeByName("BannerlordTogether.SpNativeBattle.SpNativeBattleHostMissionBehavior");
                if (host == null)
                {
                    return 0;
                }
                foreach (string name in new[] { "ReleaseHostMainFormationsToAi", "ReleaseClientOwnedFormationsToAi", "ReleaseFieldBattleSourceFormationsToAi" })
                {
                    try
                    {
                        MethodInfo method = AccessTools.Method(host, name);
                        if (method == null)
                        {
                            Log.Info(Tag + " BT release method not found (BT update?): " + name);
                            continue;
                        }
                        harmony.Patch(method, new HarmonyMethod(typeof(SiegeCommandGuard), nameof(BtReleasePrefix)), null, null,
                            new HarmonyMethod(typeof(SiegeCommandGuard), nameof(BtReleaseFinalizer)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info(Tag + " could not hook BT " + name + ": " + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " BT hook scan failed: " + ex.Message);
            }
            return count;
        }

        // ---------------------------------------------------------------- scope

        /// <summary>Mission scope: a siege assault (not a sally-out) where the player's team
        /// defends, on a machine that owns the battle (solo or BT host).</summary>
        private static bool InScope(out Mission mission)
        {
            mission = Mission.Current;
            if (mission == null || !mission.IsSiegeBattle || mission.IsSallyOutBattle)
            {
                return false;
            }
            Team playerTeam = mission.PlayerTeam;
            if (playerTeam == null || playerTeam.Side != BattleSideEnum.Defender)
            {
                return false;
            }
            return !IsBtClient();
        }

        private static bool IsGuardedFormation(Formation formation, Mission mission)
        {
            return formation != null && formation.Team == mission.PlayerTeam && mission.PlayerTeam.IsPlayerGeneral &&
                   (int)formation.FormationIndex < RegularFormationCount;
        }

        private static bool IsBtClient()
        {
            try
            {
                return PeerDetection.IsClient() == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Campaign-side truth for the role decision (available before the mission's
        /// PlayerTeam exists): the player's party is the DEFENDER of a siege assault on a
        /// settlement the player's clan owns.</summary>
        private static bool PlayerDefendsOwnSettlementInSiege()
        {
            try
            {
                if (Campaign.Current == null || MobileParty.MainParty == null)
                {
                    return false;
                }
                MapEvent mapEvent = MobileParty.MainParty.MapEvent;
                if (mapEvent == null || !mapEvent.IsSiegeAssault || mapEvent.PlayerSide != BattleSideEnum.Defender)
                {
                    return false;
                }
                Settlement settlement = mapEvent.MapEventSettlement;
                return settlement != null && settlement.OwnerClan != null && settlement.OwnerClan == Clan.PlayerClan;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The testable core: must an AI hand-off be refused? Only the player's own
        /// delegate order, vanilla's death hand-off and a BT player-down release may pass, and
        /// only regular formations of a general defending a siege after deployment are guarded.</summary>
        internal static bool ShouldRefuseHandoff(bool requestAi, bool siegeDefense, bool deploymentFinished, bool playerGeneral,
            int formationIndex, int explicitDepth, int delegateDepth, int btDepth)
        {
            return requestAi && siegeDefense && deploymentFinished && playerGeneral &&
                   formationIndex >= 0 && formationIndex < RegularFormationCount &&
                   explicitDepth <= 0 && delegateDepth <= 0 && btDepth <= 0;
        }

        // ---------------------------------------------------------------- patches

        private static void SetControlledByAIPrefix(Formation __instance, ref bool isControlledByAI)
        {
            try
            {
                if (!isControlledByAI)
                {
                    return;
                }
                Mission mission;
                if (!InScope(out mission) || !IsGuardedFormation(__instance, mission))
                {
                    return;
                }
                if (!ShouldRefuseHandoff(true, true, mission.IsDeploymentFinished, mission.PlayerTeam.IsPlayerGeneral,
                        (int)__instance.FormationIndex, _explicitAiDepth, _delegateDepth, _btReleaseDepth))
                {
                    return;
                }
                isControlledByAI = false;
                _blockedHandoffs++;
                SelfHealing.RecordFire(Component);
                LogBlocked("kept " + Name(__instance) + " under your command (refused an AI hand-off)");
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " hand-off guard error: " + ex.Message);
            }
        }

        private static bool TransferUnitsPrefix(Formation __instance, Formation target, int unitCount)
        {
            try
            {
                Mission mission;
                if (!InScope(out mission) || !mission.IsDeploymentFinished)
                {
                    return true;
                }
                bool sourceGuarded = IsGuardedFormation(__instance, mission) && !__instance.IsAIControlled;
                bool targetGuarded = IsGuardedFormation(target, mission) && !target.IsAIControlled;
                if (!sourceGuarded && !targetGuarded)
                {
                    return true;
                }
                _blockedTransfers++;
                SelfHealing.RecordFire(Component);
                LogBlocked("stopped the castle-defence AI moving " + unitCount + " troop(s) " +
                           (sourceGuarded ? "out of " + Name(__instance) : "into " + Name(target)));
                return false;
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " transfer guard error: " + ex.Message);
                return true;
            }
        }

        private static void SetPlayerRolePrefix(Team __instance, ref bool isPlayerGeneral, ref bool isPlayerSergeant)
        {
            try
            {
                if (isPlayerGeneral && !isPlayerSergeant)
                {
                    return;
                }
                if (_delegateDepth > 0 || _btReleaseDepth > 0)
                {
                    return; // death / BT release paths may demote — that is their job
                }
                if (__instance == null || __instance.Side != BattleSideEnum.Defender || IsBtClient() || !PlayerDefendsOwnSettlementInSiege())
                {
                    return;
                }
                Log.Info(Tag + " you own this settlement — you are the GENERAL of its defense (vanilla wanted general=" +
                         isPlayerGeneral + " sergeant=" + isPlayerSergeant + ": inside another lord's army you would only lead one formation)");
                isPlayerGeneral = true;
                isPlayerSergeant = false;
                SelfHealing.RecordFire(Component);
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " role guard error: " + ex.Message);
            }
        }

        private static void RoleControllerAfterStartPrefix(object __instance)
        {
            try
            {
                if (__instance == null || IsBtClient() || !PlayerDefendsOwnSettlementInSiege())
                {
                    return;
                }
                bool general = (bool)_generalField.GetValue(__instance);
                bool sergeant = (bool)_sergeantField.GetValue(__instance);
                if (general && !sergeant)
                {
                    return;
                }
                _generalField.SetValue(__instance, true);
                _sergeantField.SetValue(__instance, false);
                Log.Info(Tag + " role controller: promoted to general of your own settlement's defense (was general=" + general + " sergeant=" + sergeant + ")");
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " role controller guard error: " + ex.Message);
            }
        }

        private static void DeploymentFinishedPostfix()
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission == null || !mission.IsSiegeBattle || mission.IsSallyOutBattle || mission.PlayerTeam == null ||
                    mission.PlayerTeam.Side != BattleSideEnum.Defender)
                {
                    return;
                }
                if (IsBtClient())
                {
                    if (!_clientNoteLogged)
                    {
                        _clientNoteLogged = true;
                        Log.Info(Tag + " co-op CLIENT — the host's command assignment decides who commands what; to command every formation of your castle's defense, host the session (shared-save host handoff)");
                    }
                    return;
                }
                Team team = mission.PlayerTeam;
                if (!team.IsPlayerGeneral)
                {
                    Log.Info(Tag + " deployment finished but you are not the general of this defense (another lord's army) — vanilla command applies");
                    return;
                }
                int taken = 0;
                int held = 0;
                foreach (Formation formation in team.FormationsIncludingEmpty)
                {
                    if ((int)formation.FormationIndex >= RegularFormationCount)
                    {
                        continue;
                    }
                    if (!formation.IsAIControlled)
                    {
                        held++;
                        continue;
                    }
                    WorldPosition spot = formation.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache.GroundVec3);
                    formation.SetControlledByAI(false, false);
                    if (spot.IsValid)
                    {
                        formation.SetMovementOrder(MovementOrder.MovementOrderMove(spot));
                    }
                    taken++;
                }
                if (taken > 0)
                {
                    SelfHealing.RecordFire(Component);
                }
                Log.Info(Tag + " you command all " + (taken + held) + " formation(s) — " + taken + " taken back from the AI at their deployed spot, " +
                         held + " already yours; the castle-defence AI will not move them or re-shuffle their troops (F6 still delegates a formation on purpose)");
                if (!_screenNoteShown)
                {
                    _screenNoteShown = true;
                    Log.Screen("Siege defense: you command all " + (taken + held) + " formations — they hold where placed (F6 delegates one to the AI)");
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " deployment take-over error: " + ex.Message);
            }
        }

        private static void SetOrderPrefix(OrderType orderType)
        {
            if (orderType == OrderType.AIControlOn)
            {
                _explicitAiDepth++;
            }
        }

        private static Exception SetOrderFinalizer(OrderType orderType, Exception __exception)
        {
            if (orderType == OrderType.AIControlOn && _explicitAiDepth > 0)
            {
                _explicitAiDepth--;
            }
            return __exception;
        }

        private static void DelegatePrefix()
        {
            _delegateDepth++;
        }

        private static Exception DelegateFinalizer(Exception __exception)
        {
            if (_delegateDepth > 0)
            {
                _delegateDepth--;
            }
            return __exception;
        }

        private static void BtReleasePrefix()
        {
            _btReleaseDepth++;
        }

        private static Exception BtReleaseFinalizer(Exception __exception)
        {
            if (_btReleaseDepth > 0)
            {
                _btReleaseDepth--;
            }
            return __exception;
        }

        // ---------------------------------------------------------------- helpers

        private static string Name(Formation formation)
        {
            try
            {
                return formation.FormationIndex + "(" + formation.CountOfUnits + ")";
            }
            catch
            {
                return "formation";
            }
        }

        private static void LogBlocked(string what)
        {
            int now = Environment.TickCount;
            if (_lastBlockLogTick != 0 && now - _lastBlockLogTick < 5000 && now >= _lastBlockLogTick)
            {
                return;
            }
            _lastBlockLogTick = now;
            Log.Info(Tag + " " + what + " — this battle: " + _blockedHandoffs + " hand-off(s) refused, " + _blockedTransfers + " troop shuffle(s) stopped");
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type deploymentHandler = AccessTools.TypeByName("TaleWorlds.MountAndBlade.Missions.Handlers.BattleDeploymentHandler");
            bool resolved = AccessTools.Method(typeof(Formation), "SetControlledByAI", new[] { typeof(bool), typeof(bool) }) != null &&
                            AccessTools.Method(typeof(Formation), "TransferUnits", new[] { typeof(Formation), typeof(int) }) != null &&
                            AccessTools.Method(typeof(Team), "SetPlayerRole", new[] { typeof(bool), typeof(bool) }) != null &&
                            AccessTools.Method(typeof(Team), "DelegateCommandToAI") != null &&
                            AccessTools.Method(typeof(OrderController), "SetOrder", new[] { typeof(OrderType) }) != null &&
                            AccessTools.Method(typeof(Mission), "OnDeploymentFinished") != null &&
                            AccessTools.Method(typeof(MovementOrder), "MovementOrderMove", new[] { typeof(WorldPosition) }) != null &&
                            AccessTools.Method(typeof(Formation), "CreateNewOrderWorldPosition", new[] { typeof(WorldPosition.WorldPositionEnforcedCache) }) != null &&
                            (int)OrderType.AIControlOn == 36 &&
                            deploymentHandler != null && AccessTools.Method(deploymentHandler, "SetDefaultFormationOrders") != null;
            bool decisions =
                ShouldRefuseHandoff(true, true, true, true, 3, 0, 0, 0) &&
                ShouldRefuseHandoff(true, true, true, true, 0, 0, 0, 0) &&
                ShouldRefuseHandoff(true, true, true, true, 7, 0, 0, 0) &&
                !ShouldRefuseHandoff(false, true, true, true, 3, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, false, true, true, 3, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, true, false, true, 3, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, true, true, false, 3, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, true, true, true, 8, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, true, true, true, 9, 0, 0, 0) &&
                !ShouldRefuseHandoff(true, true, true, true, 3, 1, 0, 0) &&
                !ShouldRefuseHandoff(true, true, true, true, 3, 0, 1, 0) &&
                !ShouldRefuseHandoff(true, true, true, true, 3, 0, 0, 1);
            bool pass = resolved && decisions;
            return SelfHealing.TestResult.Of("siege-command-guard.contract", pass,
                pass ? "members re-resolved (incl. vanilla's siege AI-on default); hand-off decision table verified"
                     : "resolved=" + resolved + " decisions=" + decisions);
        }
    }
}
