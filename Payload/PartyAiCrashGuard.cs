using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards the campaign party-AI tick against half-synced party state.
    ///
    /// Crash (2026-08-19, host, the moment a co-op client joined):
    /// NullReferenceException in MobilePartyAi.GetBehaviors via Campaign.PartiesThink.
    /// IL at the fault site (~04B4): the DefendSettlement branch reads
    /// _mobileParty.TargetSettlement, and when that is null falls back to
    /// targetParty.TargetSettlement — with BOTH null it dereferences null. Vanilla
    /// never produces DefendSettlement with no target settlement AND no target party;
    /// a party whose fields are synced piecemeal during a co-op join can, for a few
    /// ticks, until the rest of its state arrives.
    ///
    /// Layer 1 — prefix on MobilePartyAi.Tick: skip the tick for a party in exactly
    /// that proven-inconsistent state (it self-heals when sync completes). Note this
    /// is a PREFIX that runs on every party tick, not a finalizer — it changes
    /// behaviour (skips one party's tick) without any exception in sight.
    /// Layer 2 — finalizer on GetBehaviors: any escaping exception becomes
    /// "Hold at current position this tick" instead of a crash-to-desktop.
    /// Layer 3 — finalizer on EncounterManager.HandleEncounterForMobileParty.
    /// </summary>
    internal static class PartyAiCrashGuard
    {
        internal const string Component = "party-ai-guard";
        private const string Tag = "[AI-GUARD]";
        private static bool _applied;
        private static FieldInfo _mobilePartyField;
        private static int _lastSkipLogTick;
        private static int _skipsSinceLog;

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
                _mobilePartyField = AccessTools.Field(typeof(MobilePartyAi), "_mobileParty");
                int count = 0;
                MethodInfo tick = AccessTools.Method(typeof(MobilePartyAi), "Tick");
                if (tick != null)
                {
                    harmony.Patch(tick, new HarmonyMethod(typeof(PartyAiCrashGuard), nameof(TickPrefix)));
                    count++;
                }
                MethodInfo getBehaviors = AccessTools.Method(typeof(MobilePartyAi), "GetBehaviors");
                if (getBehaviors != null)
                {
                    harmony.Patch(getBehaviors, null, null, null, new HarmonyMethod(typeof(PartyAiCrashGuard), nameof(GetBehaviorsFinalizer)));
                    count++;
                }
                MethodInfo handleEncounter = AccessTools.Method(typeof(EncounterManager), "HandleEncounterForMobileParty");
                if (handleEncounter != null)
                {
                    harmony.Patch(handleEncounter, null, null, null, new HarmonyMethod(typeof(PartyAiCrashGuard), nameof(HandleEncounterFinalizer)));
                    count++;
                }
                bool ok = count == 3 && _mobilePartyField != null;
                string detail = count + "/3 method(s) patched, _mobileParty field " + (_mobilePartyField != null ? "resolved" : "MISSING (layer 1 inert)");
                Diag.Report(Component, ok, ok ? "" : detail);
                Log.Info(Tag + " party-AI crash guard " + (ok ? "active" : "DEGRADED") + " — " + detail);
            }
            catch (Exception ex)
            {
                Diag.Report(Component, false, ex.Message);
                Log.Info(Tag + " apply failed: " + ex.Message);
            }
        }

        private static MobileParty PartyOf(MobilePartyAi ai)
        {
            try
            {
                return _mobilePartyField != null ? _mobilePartyField.GetValue(ai) as MobileParty : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool TickPrefix(MobilePartyAi __instance)
        {
            try
            {
                MobileParty party = PartyOf(__instance);
                if (party == null)
                {
                    return true;
                }
                if (party.DefaultBehavior == AiBehavior.DefendSettlement &&
                    party.TargetSettlement == null &&
                    party.TargetParty == null &&
                    party.ShortTermTargetParty == null)
                {
                    LogSkip(party);
                    return false; // half-synced defend-settlement state; skip until sync completes
                }
            }
            catch
            {
            }
            return true;
        }

        internal static Exception GetBehaviorsFinalizer(Exception __exception, MobilePartyAi __instance,
            ref AiBehavior bestAiBehavior, ref IInteractablePoint behaviorObject, ref CampaignVec2 bestTargetPoint)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SelfHealing.RecordFire(Component);
                MobileParty party = PartyOf(__instance);
                bestAiBehavior = AiBehavior.Hold;
                behaviorObject = null;
                bestTargetPoint = party != null ? party.Position : default(CampaignVec2);
                Log.Info(Tag + " SUPPRESSED crash in MobilePartyAi.GetBehaviors for " +
                         (party != null ? party.StringId : "?") + " — forced Hold this tick: " + __exception.Message);
            }
            catch (Exception exRecovery)
            {
                Log.Info(Tag + " recovery failed: " + exRecovery.Message);
            }
            return null;
        }

        /// <summary>
        /// Second guarded organ of the same disease (crash 2026-08-19 ~20:28): the
        /// per-party encounter handling in the campaign tick NREs on a half-synced
        /// party. Skipping one party's encounter handling for a tick is benign — it
        /// reruns next tick, and the party heals when its sync completes.
        /// </summary>
        internal static Exception HandleEncounterFinalizer(Exception __exception, MobileParty mobileParty)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SelfHealing.RecordFire(Component);
                Log.Info(Tag + " SUPPRESSED crash in EncounterManager.HandleEncounterForMobileParty for " +
                         (mobileParty != null ? mobileParty.StringId : "?") + ": " + __exception.Message);
            }
            catch
            {
            }
            return null;
        }

        private static void LogSkip(MobileParty party)
        {
            try
            {
                _skipsSinceLog++;
                int now = Environment.TickCount;
                if (_lastSkipLogTick != 0 && now - _lastSkipLogTick < 5000 && now >= _lastSkipLogTick)
                {
                    return;
                }
                _lastSkipLogTick = now;
                Log.Info(Tag + " skipping AI tick for half-synced party " + party.StringId +
                         " (DefendSettlement with no target; " + _skipsSinceLog + " skip(s) since last report)");
                _skipsSinceLog = 0;
            }
            catch
            {
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = AccessTools.Field(typeof(MobilePartyAi), "_mobileParty") != null &&
                            AccessTools.Method(typeof(MobilePartyAi), "Tick") != null &&
                            AccessTools.Method(typeof(MobilePartyAi), "GetBehaviors") != null &&
                            AccessTools.Method(typeof(EncounterManager), "HandleEncounterForMobileParty") != null;
            AiBehavior behavior = default(AiBehavior);
            IInteractablePoint point = null;
            CampaignVec2 target = default(CampaignVec2);
            bool inert = TickPrefix(null) &&
                         GetBehaviorsFinalizer(null, null, ref behavior, ref point, ref target) == null &&
                         HandleEncounterFinalizer(null, null) == null;
            bool pass = resolved && inert;
            return SelfHealing.TestResult.Of("party-ai-guard.contract", pass,
                pass ? "all three targets + _mobileParty re-resolved; prefix passes through on null, finalizers inert on null exception"
                     : "resolved=" + resolved + " inert=" + inert);
        }
    }
}
