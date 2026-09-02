using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// "Create New Party" made observable AND usable (field report 2026-09-01: "I made a
    /// party and it didn't allow me to add anyone"; follow-up: "it should happen on creation,
    /// not when I click the new party"). Decoded from the installed build's IL
    /// (ClanPartiesVM, TaleWorlds.CampaignSystem.ViewModelCollection):
    ///  - the leader popup lists Clan.Heroes + Clan.Companions; a card is DISABLED with a
    ///    reason when the hero is a prisoner / released / fugitive, a child, in someone else's
    ///    party, leading a party, a governor, in the Disabled state, at sea, or when
    ///    hero.Gold + MainHero.Gold is under ClanFinanceModel.PartyGoldLowerThreshold;
    ///  - the button is disabled for prisoner / no free war-party slot (Clan.WarPartyLimit) /
    ///    no available hero / not enough gold;
    ///  - CreateNewClanParty creates the party with the LEADER ONLY (hero removed from the
    ///    main party, party spawned beside it, set to hold) — vanilla has no troop step; the
    ///    player is expected to meet the party on the map and use "Manage troops".
    ///
    /// Enhancement (config partyTroopsOnCreate, default on): right after creation, open
    /// vanilla's own manage-troops party screen against the new party
    /// (PartyScreenHelper.OpenScreenAsManageTroops — the same call the "manage garrison"
    /// menu and the clan-member conversation use), deferred to the next tick so the clan
    /// screen's popup/inquiry finishes first, with the clan screen popped so the party
    /// screen sits on the map exactly like vanilla's own flows. On a BannerlordTogether
    /// CLIENT the local party is provisional (BT's ClientWarPartyCreationPatch registers a
    /// pending host-side creation), so the screen opens only once the same party instance
    /// has stayed alive and led by the hero for a short settle window.
    ///
    /// Observability: the button's disabled reason and every candidate's greyed-out reason
    /// are logged when the popup opens (the vanilla iterator is enumerated for logging only —
    /// it yields a fresh enumerator per call; the result is never replaced). Reflection-only
    /// for the ViewModelCollection types (no compile-time reference).
    /// </summary>
    internal static class ClanPartyCreationAdvisor
    {
        private const string VmType = "TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.Categories.ClanPartiesVM";

        /// <summary>How long a BT client's provisional party must stay stable before the
        /// troop screen opens against it.</summary>
        private const int ClientSettleMs = 3000;
        private const int PendingTimeoutMs = 15000;

        private static bool _autoOpen;
        private static Hero _pendingLeader;
        private static MobileParty _pendingParty;
        private static int _pendingSinceTick;
        private static int _openNotBeforeTick;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _autoOpen = GuardConfig.Bool("partyTroopsOnCreate", true);
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
                Log.Info("[CLAN-PARTY] create-party advisor active on " + patched + " method(s) — leader list + greyed-out reasons logged; troop screen on creation=" + _autoOpen.ToString().ToLowerInvariant());
                Diag.Report("clan-party-advisor", patched > 0, patched > 0 ? "" : "no methods resolved");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] apply failed: " + ex.Message);
                Diag.Report("clan-party-advisor", false, ex.Message);
            }
        }

        // ---- observability ----------------------------------------------------------------

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

        /// <summary>Enumerates the vanilla result for logging only. The result is a C#
        /// iterator (fresh enumerator per GetEnumerator, no side effects) and is NEVER
        /// replaced — the VM foreach-es its own generic type.</summary>
        private static void CandidatesPostfix(IEnumerable __result)
        {
            try
            {
                if (__result == null)
                {
                    return;
                }
                int total = 0, enabled = 0;
                foreach (object item in __result)
                {
                    total++;
                    Type t = item.GetType();
                    string title = Text(Member(t, item, "Title") as TextObject);
                    bool disabled = Member(t, item, "IsDisabled") is bool b && b;
                    string reason = Text(Member(t, item, "DisabledReason") as TextObject);
                    if (!disabled)
                    {
                        enabled++;
                    }
                    Log.Info("[CLAN-PARTY]   candidate " + title + ": " + (disabled ? "GREYED OUT — " + reason : "selectable"));
                }
                Log.Info("[CLAN-PARTY] leader popup: " + total + " candidate(s), " + enabled + " selectable (war parties " +
                         WarPartyUse() + ", gold threshold " + Threshold() + ")");
                if (total > 0 && enabled == 0)
                {
                    Log.Screen("no clan member can lead a new party right now — hover a greyed card for the reason (details in CrashGuard.log)");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] candidate log failed: " + ex.Message);
            }
        }

        /// <summary>Field first (the card info type uses public fields), property as fallback.</summary>
        private static object Member(Type t, object instance, string name)
        {
            FieldInfo f = AccessTools.Field(t, name);
            if (f != null)
            {
                return f.GetValue(instance);
            }
            PropertyInfo p = AccessTools.Property(t, name);
            return p != null ? p.GetValue(instance) : null;
        }

        // ---- the enhancement: troop screen on creation ---------------------------------------

        private static void CreatedPostfix(Hero newLeader)
        {
            try
            {
                string name = newLeader != null && newLeader.Name != null ? newLeader.Name.ToString() : "?";
                MobileParty party = newLeader != null ? newLeader.PartyBelongedTo : null;
                bool ours = party != null && party != MobileParty.MainParty && party.LeaderHero == newLeader;
                Log.Info("[CLAN-PARTY] party created with leader " + name + (ours ? " (" + party.StringId + ")" : " (party not resolved yet)") +
                         " — vanilla creates it with the leader ONLY");
                if (!_autoOpen || newLeader == null)
                {
                    Log.Screen(name + ": new party created with no troops yet — click it on the map and exchange troops to fill it");
                    return;
                }
                // Defer to the main-thread Tick: the clan screen's popup + inquiry are still
                // unwinding on this call stack, and a BT client's party is provisional.
                _pendingLeader = newLeader;
                _pendingParty = ours ? party : null;
                _pendingSinceTick = Environment.TickCount;
                _openNotBeforeTick = _pendingSinceTick + (PeerDetection.IsClient() == true ? ClientSettleMs : 0);
                Log.Screen(name + ": party created — opening the troop exchange");
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] created-postfix error: " + ex.Message);
            }
        }

        /// <summary>Main-thread follow-up: open the manage-troops screen once the party is
        /// resolved and stable, or give up with a clear note after the timeout.</summary>
        internal static void Tick()
        {
            if (_pendingLeader == null)
            {
                return;
            }
            try
            {
                int now = Environment.TickCount;
                if (now - _pendingSinceTick > PendingTimeoutMs)
                {
                    Log.Info("[CLAN-PARTY] gave up opening the troop screen for " + Name(_pendingLeader) + " — no stable party led by them appeared within " + (PendingTimeoutMs / 1000) + "s");
                    Log.Screen(Name(_pendingLeader) + "'s party: could not open the troop exchange automatically — click the party on the map to fill it");
                    ClearPending();
                    return;
                }
                if (now < _openNotBeforeTick)
                {
                    return; // BT client settle window
                }
                MobileParty party = _pendingLeader.PartyBelongedTo;
                if (party == null || party == MobileParty.MainParty || party.LeaderHero != _pendingLeader || !party.IsActive)
                {
                    return; // not there yet (BT client) — keep waiting
                }
                if (_pendingParty != null && !ReferenceEquals(_pendingParty, party))
                {
                    // BT replaced the provisional party with the host-authoritative one —
                    // restart the settle window on the new instance.
                    _pendingParty = party;
                    _openNotBeforeTick = now + ClientSettleMs;
                    Log.Info("[CLAN-PARTY] party instance for " + Name(_pendingLeader) + " changed (co-op reconciliation) — waiting for it to settle");
                    return;
                }
                if (TaleWorlds.MountAndBlade.Mission.Current != null)
                {
                    return; // never push a party screen over a mission
                }
                GameStateManager gsm = Game.Current != null ? Game.Current.GameStateManager : null;
                if (gsm == null)
                {
                    return;
                }
                if (gsm.ActiveState is PartyState)
                {
                    return; // a party screen is already up (the player opened one) — wait for it to close
                }
                if (gsm.ActiveState is ClanState)
                {
                    gsm.PopState(0); // land on the map first, like vanilla's own manage-troops flows
                }
                if (!(gsm.ActiveState is MapState))
                {
                    return; // some other screen (inquiry, encyclopedia) — try again next tick
                }
                PartyScreenHelper.OpenScreenAsManageTroops(party);
                SelfHealing.RecordFire("clan-party-advisor");
                Log.Info("[CLAN-PARTY] opened the troop exchange with " + Name(_pendingLeader) + "'s new party (" + party.StringId + ") on creation");
                ClearPending();
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-PARTY] auto-open failed: " + ex.Message + " — click the party on the map to fill it");
                Log.Screen("could not open the troop exchange automatically — click the new party on the map to fill it");
                ClearPending();
            }
        }

        private static void ClearPending()
        {
            _pendingLeader = null;
            _pendingParty = null;
        }

        // ---- helpers ------------------------------------------------------------------------

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

        private static string Name(Hero hero)
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

        private static SelfHealing.TestResult SelfTest()
        {
            Type vm = AccessTools.TypeByName(VmType);
            bool resolved = vm != null &&
                            AccessTools.Method(vm, "GetNewPartyLeaderCandidates") != null &&
                            AccessTools.Method(vm, "CreateNewClanParty") != null;
            Type info = AccessTools.TypeByName("TaleWorlds.CampaignSystem.ViewModelCollection.ClanManagement.ClanCardSelectionItemInfo");
            bool shape = info != null &&
                         (AccessTools.Field(info, "IsDisabled") != null || AccessTools.Property(info, "IsDisabled") != null) &&
                         (AccessTools.Field(info, "DisabledReason") != null || AccessTools.Property(info, "DisabledReason") != null) &&
                         (AccessTools.Field(info, "Title") != null || AccessTools.Property(info, "Title") != null);
            bool opener = AccessTools.Method(typeof(PartyScreenHelper), "OpenScreenAsManageTroops", new[] { typeof(MobileParty) }) != null;
            bool pass = resolved && shape && opener;
            return SelfHealing.TestResult.Of("clan-party-advisor.contract", pass,
                pass ? "ClanPartiesVM + card-info shape + OpenScreenAsManageTroops re-resolved"
                     : "resolved=" + resolved + " shape=" + shape + " opener=" + opener);
        }
    }
}
