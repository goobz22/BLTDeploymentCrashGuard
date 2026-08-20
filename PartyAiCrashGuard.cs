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
    /// that proven-inconsistent state (it self-heals when sync completes).
    /// Layer 2 — finalizer on GetBehaviors: any escaping exception becomes
    /// "Hold at current position this tick" instead of a crash-to-desktop.
    /// </summary>
    internal static class PartyAiCrashGuard
    {
        private static FieldInfo _mobilePartyField;
        private static int _lastSkipLogTick;
        private static int _skipsSinceLog;

        internal static void Apply(Harmony harmony)
        {
            try
            {
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
                Log.Info("[AI-GUARD] party-AI crash guard active on " + count + " method(s)");
            }
            catch (Exception ex)
            {
                Log.Info("[AI-GUARD] apply failed: " + ex.Message);
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

        private static bool TickPrefix(MobilePartyAi __instance)
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

        private static Exception GetBehaviorsFinalizer(Exception __exception, MobilePartyAi __instance,
            ref AiBehavior bestAiBehavior, ref IInteractablePoint behaviorObject, ref CampaignVec2 bestTargetPoint)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                MobileParty party = PartyOf(__instance);
                bestAiBehavior = AiBehavior.Hold;
                behaviorObject = null;
                bestTargetPoint = party != null ? party.Position : default(CampaignVec2);
                Log.Info("[AI-GUARD] SUPPRESSED crash in MobilePartyAi.GetBehaviors for " +
                         (party != null ? party.StringId : "?") + " — forced Hold this tick: " + __exception.Message);
            }
            catch (Exception exRecovery)
            {
                Log.Info("[AI-GUARD] recovery failed: " + exRecovery.Message);
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
                Log.Info("[AI-GUARD] skipping AI tick for half-synced party " + party.StringId +
                         " (DefendSettlement with no target; " + _skipsSinceLog + " skip(s) since last report)");
                _skipsSinceLog = 0;
            }
            catch
            {
            }
        }
    }
}
