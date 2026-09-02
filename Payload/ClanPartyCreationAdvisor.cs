using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Makes "Create New Party" observable (field report 2026-09-01: "I made a party and it
    /// didn't allow me to add anyone"). Decoded from the installed build's IL
    /// (ClanPartiesVM, TaleWorlds.CampaignSystem.ViewModelCollection):
    ///  - the leader popup lists Clan.Heroes + Clan.Companions; each card is DISABLED with a
    ///    reason when the hero is a prisoner / released / fugitive, a child, already in a party
    ///    that is not the player's, leading their own party, a governor, in the Disabled state,
    ///    at sea, or when hero.Gold + MainHero.Gold is under the finance model's
    ///    PartyGoldLowerThreshold ("not enough gold");
    ///  - the button itself is disabled for: prisoner, no empty war-party slot
    ///    (Clan.WarPartyLimit), no available hero, not enough gold;
    ///  - on confirm, vanilla creates the party with the LEADER ONLY (removes the hero from
    ///    the main party, spawns beside it, holds position) — there is no troop-transfer step;
    ///    troops are handed over afterwards by meeting the new party on the map.
    /// BannerlordTogether does not patch this path (assembly scan). This advisor logs the
    /// button reason, every candidate with its disabled reason, and an on-screen note after
    /// creation explaining where the troops come from. Reflection-only (no compile-time
    /// reference to the ViewModelCollection assembly).
    /// </summary>
    internal static class ClanPartyCreationAdvisor
    {
        private const string VmType = "TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanPartiesVM";

        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type vm = AccessTools.TypeByName(VmType);
                if (vm == null)
                {
                    Log.Info("[CLAN-PARTY] ClanPartiesVM not found — advisor inactive (game update?)");
                    Diag.Report("clan-party-advisor", false, "ClanPartiesVM not found");
                    return;
                }
                int patched = 0;
                MethodInfo candidates = AccessTools.Method(vm, "GetNewPartyLeaderCandidates");
                if (candidates != null)
                {
                    harmony.Patch(candidates, null, new HarmonyMethod(typeof(ClanPartyCreationAdvisor), nameof(CandidatesPostfix)));
                    patched++;
                }
                MethodInfo canCreate = AccessTools.Method(vm, "GetCanCreateNewParty");
                if (canCreate != null)
                {
                    harmony.Patch(canCreate, null, new HarmonyMethod(typeof(ClanPartyCreationAdvisor), nameof(CanCreatePostfix)));
                    patched++;
                }
                MethodInfo create = AccessTools.Method(vm, "CreateNewClanParty");
                if (create != null)
                {
                    harmony.Patch(create, null, new HarmonyMethod(typeof(ClanPartyCreationAdvisor), nameof(CreatedPostfix)));
                    patched++;
                }
                Log.Info("[CLAN-PARTY] create-party advisor active on " + patched + " method(s) — the leader list and every greyed-out reason are logged when the popup opens");
                Diag.Report("clan-party-advisor", patched > 0, patched > 0 ? "" : "no methods resolved");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] apply failed: " + ex.Message);
                Diag.Report("clan-party-advisor", false, ex.Message);
            }
        }

        private static void CanCreatePostfix(bool __result, TextObject disabledReason)
        {
            try
            {
                if (!__result)
                {
                    Log.Info("[CLAN-PARTY] Create New Party button DISABLED: " + Text(disabledReason) +
                             " (war parties " + WarPartyUse() + ", gold " + (Hero.MainHero != null ? Hero.MainHero.Gold.ToString() : "?") +
                             ", threshold " + Threshold() + ")");
                }
            }
            catch
            {
            }
        }

        private static void CandidatesPostfix(ref IEnumerable __result)
        {
            try
            {
                if (__result == null)
                {
                    return;
                }
                // Materialize once (the vanilla iterator has no side effects) so the popup and
                // the log see the same list.
                var items = new ArrayList();
                foreach (object item in __result)
                {
                    items.Add(item);
                }
                __result = items;
                int enabled = 0;
                foreach (object item in items)
                {
                    Type t = item.GetType();
                    string title = Text(AccessTools.Field(t, "Title")?.GetValue(item) as TextObject);
                    bool disabled = AccessTools.Field(t, "IsDisabled")?.GetValue(item) is bool b && b;
                    string reason = Text(AccessTools.Field(t, "DisabledReason")?.GetValue(item) as TextObject);
                    if (!disabled)
                    {
                        enabled++;
                    }
                    Log.Info("[CLAN-PARTY]   candidate " + title + ": " + (disabled ? "GREYED OUT — " + reason : "selectable"));
                }
                Log.Info("[CLAN-PARTY] leader popup: " + items.Count + " candidate(s), " + enabled + " selectable (war parties " +
                         WarPartyUse() + ", gold threshold " + Threshold() + ")");
                if (items.Count > 0 && enabled == 0)
                {
                    Log.Screen("no clan member can lead a new party right now — hover a greyed card for the reason (details in CrashGuard.log)");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] candidate log failed: " + ex.Message);
            }
        }

        private static void CreatedPostfix(Hero newLeader)
        {
            try
            {
                string name = newLeader != null && newLeader.Name != null ? newLeader.Name.ToString() : "?";
                Log.Info("[CLAN-PARTY] party created with leader " + name + " — vanilla creates it with the leader ONLY; " +
                         "hand over troops by meeting the party on the map (it holds position beside you)");
                Log.Screen(name + ": new party created with no troops yet — click it on the map and exchange troops to fill it");
            }
            catch
            {
            }
        }

        private static string WarPartyUse()
        {
            try
            {
                Clan clan = Clan.PlayerClan;
                return clan != null ? clan.WarPartyComponents.Count + "/" + clan.WarPartyLimit : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string Threshold()
        {
            try
            {
                return Campaign.Current.Models.ClanFinanceModel.PartyGoldLowerThreshold.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string Text(TextObject t)
        {
            try
            {
                return t != null ? t.ToString() : "";
            }
            catch
            {
                return "?";
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type vm = AccessTools.TypeByName(VmType);
            bool resolved = vm != null &&
                            AccessTools.Method(vm, "GetNewPartyLeaderCandidates") != null &&
                            AccessTools.Method(vm, "CreateNewClanParty") != null;
            Type info = AccessTools.TypeByName("TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.ClanCardSelectionItemInfo");
            bool shape = info != null && AccessTools.Field(info, "IsDisabled") != null && AccessTools.Field(info, "DisabledReason") != null && AccessTools.Field(info, "Title") != null;
            bool pass = resolved && shape;
            return SelfHealing.TestResult.Of("clan-party-advisor.contract", pass,
                pass ? "ClanPartiesVM + card-info shape re-resolved" : "resolved=" + resolved + " shape=" + shape);
        }
    }
}
