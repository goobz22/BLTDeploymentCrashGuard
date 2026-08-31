using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BLTDeploymentCrashGuard.StashSync
{
    /// <summary>
    /// Co-op settlement-stash sync. BannerlordTogether has NO stash code at all (assembly
    /// scan 2026-08-30: zero stash-named members) while it DOES sync the workshop warehouse
    /// (WorkshopWarehouseRosterInventoryDonePatch), so a stash deposit made on one machine
    /// exists only there — same-clan players do NOT share a stash, and a client's deposits
    /// silently diverge from the authoritative host state (lost on resync/save-load).
    ///
    /// Fix, modeled on BT's own warehouse sync + this mod's proven PregnancySync transport:
    ///  SEND: postfix on InventoryLogic.DoneLogic (the same commit point BT patches for the
    ///        warehouse) — when a Stash-mode inventory screen commits, snapshot the CURRENT
    ///        settlement's whole stash roster and send it over BT's channel (host:
    ///        Server.BroadcastRawReliableOrdered, client: Client.SendRaw — reflection only,
    ///        never compiled against BT).
    ///  RECEIVE: prefix on BT's ShouldAcceptIncomingPacket recognizes our "BTCS" frame,
    ///        queues it (network thread!), and consumes it. The main-thread Tick applies:
    ///        find the settlement, replace the stash contents. The HOST re-broadcasts an
    ///        applied client update so every peer converges (the origin re-applies its own
    ///        identical state — idempotent; applying never sends, so no echo loop).
    ///  Applying is DEFERRED while the local player has a stash screen open (the screen
    ///  works on the live roster); last-closed screen wins on a simultaneous edit.
    ///
    /// Full-snapshot semantics: idempotent, ordering-immune, converges in one packet.
    /// Limitation: an item the receiver cannot resolve by StringId (player-crafted weapons
    /// exist only on the crafting machine) is skipped with a loud log — crafted-item
    /// replication needs design serialization (recorded in UPSTREAM_BUG_REPORT.md).
    /// Gated on config stashSync (default ON) AND an active BT session; inert otherwise.
    /// </summary>
    internal static class StashSyncGuard
    {
        private static bool _enabled;
        private static FieldInfo _inventoryModeField;
        private const int StashMode = 3; // Helpers.InventoryScreenHelper.InventoryMode.Stash

        private static readonly object QueueLock = new object();
        private static readonly Queue<StashPayloadData> Pending = new Queue<StashPayloadData>();

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _enabled = GuardConfig.Bool("stashSync", true);
                SelfHealing.RegisterTest(LoopbackSelfTest); // proves the wiring even when disabled
                if (!_enabled)
                {
                    Diag.Report("stash-sync", true, "disabled by config");
                    return;
                }
                _inventoryModeField = AccessTools.Field(typeof(InventoryLogic), "_inventoryMode");
                MethodInfo done = AccessTools.Method(typeof(InventoryLogic), "DoneLogic");
                bool donePatched = false;
                if (done != null && _inventoryModeField != null)
                {
                    harmony.Patch(done, null, new HarmonyMethod(typeof(StashSyncGuard), nameof(DoneLogicPostfix)));
                    donePatched = true;
                }
                bool receiveHooked = HookReceive(harmony);
                bool ok = donePatched && receiveHooked;
                Log.Info("[STASH-SYNC] stash sync " + (ok ? "active" : "DEGRADED") +
                         " (doneLogic=" + donePatched + " receive=" + receiveHooked +
                         ") — settlement stashes stay identical on every machine");
                Diag.Report("stash-sync", ok, ok ? "" : "doneLogic=" + donePatched + " receive=" + receiveHooked);
            }
            catch (Exception ex)
            {
                Log.Info("[STASH-SYNC] apply failed: " + ex.Message);
                Diag.Report("stash-sync", false, ex.Message);
            }
        }

        private static bool HookReceive(Harmony harmony)
        {
            bool any = false;
            foreach (string typeName in new[] { "BannerlordTogether.CoopNetworkBase", "BannerlordTogether.CoopServer" })
            {
                Type type = AccessTools.TypeByName(typeName);
                MethodInfo method = type != null ? AccessTools.Method(type, "ShouldAcceptIncomingPacket") : null;
                if (method != null)
                {
                    harmony.Patch(method, new HarmonyMethod(typeof(StashSyncGuard), nameof(ShouldAcceptIncomingPacketPrefix)));
                    any = true;
                }
            }
            return any;
        }

        // ---- SEND: a local stash screen committed --------------------------------------------

        private static void DoneLogicPostfix(InventoryLogic __instance, bool __result)
        {
            try
            {
                if (!_enabled || !__result || __instance == null)
                {
                    return;
                }
                object modeValue = _inventoryModeField != null ? _inventoryModeField.GetValue(__instance) : null;
                if (modeValue == null || Convert.ToInt32(modeValue) != StashMode)
                {
                    return;
                }
                bool isHost = PeerDetection.ReadCoopStaticBool("IsHost") == true;
                bool isClient = PeerDetection.IsClient() == true;
                if (!isHost && !isClient)
                {
                    return; // no BT session — vanilla singleplayer needs no sync
                }
                if (isHost && PeerDetection.AnyRemotePeerConnected() != true)
                {
                    return; // hosting alone — nobody to tell
                }
                Settlement settlement = Settlement.CurrentSettlement;
                ItemRoster stash = settlement != null ? settlement.Stash : null;
                if (stash == null)
                {
                    Log.Info("[STASH-SYNC] stash screen committed but no current settlement/stash resolved — not synced");
                    return;
                }
                StashPayloadData payload = BuildPayload(settlement.StringId, stash);
                if (!Send(StashWireFraming.Frame(payload), isHost))
                {
                    Log.Info("[STASH-SYNC] could not send stash update (send reflection failed) — peers will diverge until the next stash edit");
                    return;
                }
                SelfHealing.RecordFire("stash-sync");
                Log.Info("[STASH-SYNC] sent stash of " + settlement.StringId + " (" + payload.Entries.Count +
                         " stack(s)) as " + (isHost ? "host broadcast" : "client update"));
            }
            catch (Exception ex)
            {
                Log.Info("[STASH-SYNC] send error: " + ex.Message);
            }
        }

        internal static StashPayloadData BuildPayload(string settlementId, ItemRoster roster)
        {
            var payload = new StashPayloadData { SettlementStringId = settlementId ?? "" };
            for (int i = 0; i < roster.Count; i++)
            {
                ItemRosterElement element = roster.GetElementCopyAtIndex(i);
                ItemObject item = element.EquipmentElement.Item;
                if (item == null || element.Amount == 0)
                {
                    continue;
                }
                payload.Entries.Add(new StashPayloadData.Entry
                {
                    ItemStringId = item.StringId ?? "",
                    ModifierStringId = element.EquipmentElement.ItemModifier != null
                        ? element.EquipmentElement.ItemModifier.StringId ?? "" : "",
                    Count = element.Amount
                });
            }
            return payload;
        }

        // ---- RECEIVE: a peer's stash state arrived -------------------------------------------

        private static bool ShouldAcceptIncomingPacketPrefix(byte[] data, ref bool __result)
        {
            try
            {
                if (!_enabled || !StashWireFraming.IsOurPacket(data))
                {
                    return true; // not ours — let BT (and the birth hook) decide normally
                }
                StashPayloadData payload = StashWireFraming.TryUnframe(data);
                if (payload != null)
                {
                    // BT's network thread — roster mutation must wait for the main-thread Tick.
                    lock (QueueLock)
                    {
                        Pending.Enqueue(payload);
                    }
                }
                else
                {
                    Log.Info("[STASH-SYNC] received a malformed stash packet — dropped");
                }
                __result = false; // consume: BT must not enqueue/dispatch our packet
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("[STASH-SYNC] receive error: " + ex.Message);
                return true;
            }
        }

        /// <summary>Drain queued stash updates on the MAIN game thread.</summary>
        internal static void Tick()
        {
            if (!_enabled)
            {
                return;
            }
            while (true)
            {
                StashPayloadData next;
                lock (QueueLock)
                {
                    if (Pending.Count == 0)
                    {
                        return;
                    }
                    if (IsLocalStashScreenOpen())
                    {
                        return; // the open screen works on the live roster — apply after it closes
                    }
                    next = Pending.Dequeue();
                }
                try
                {
                    ApplyPayload(next);
                }
                catch (Exception ex)
                {
                    Log.Info("[STASH-SYNC] apply drain error, dropped one update: " + ex.Message);
                }
            }
        }

        private static void ApplyPayload(StashPayloadData payload)
        {
            if (Campaign.Current == null || payload == null || string.IsNullOrEmpty(payload.SettlementStringId))
            {
                return;
            }
            Settlement settlement = null;
            foreach (Settlement s in Settlement.All)
            {
                if (s != null && s.StringId == payload.SettlementStringId)
                {
                    settlement = s;
                    break;
                }
            }
            ItemRoster stash = settlement != null ? settlement.Stash : null;
            if (stash == null)
            {
                Log.Info("[STASH-SYNC] received stash for unknown settlement '" + payload.SettlementStringId + "' — dropped");
                return;
            }
            int before = stash.Count;
            stash.Clear();
            int applied = 0, skipped = 0;
            foreach (StashPayloadData.Entry entry in payload.Entries)
            {
                ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(entry.ItemStringId);
                if (item == null)
                {
                    skipped++;
                    Log.Info("[STASH-SYNC] cannot resolve item '" + entry.ItemStringId + "' on this machine (player-crafted?) — stack skipped");
                    continue;
                }
                ItemModifier modifier = string.IsNullOrEmpty(entry.ModifierStringId)
                    ? null : MBObjectManager.Instance.GetObject<ItemModifier>(entry.ModifierStringId);
                stash.AddToCounts(new EquipmentElement(item, modifier), entry.Count);
                applied++;
            }
            SelfHealing.RecordFire("stash-sync");
            Log.Info("[STASH-SYNC] applied stash of " + payload.SettlementStringId + ": " + before +
                     " -> " + applied + " stack(s)" + (skipped > 0 ? " (" + skipped + " unresolvable stack(s) SKIPPED)" : ""));
            // The host relays an applied client update so every peer converges; the origin
            // client just re-applies its own identical state (idempotent — apply never sends).
            if (PeerDetection.ReadCoopStaticBool("IsHost") == true && PeerDetection.AnyRemotePeerConnected() == true)
            {
                Send(StashWireFraming.Frame(payload), isHost: true);
            }
        }

        /// <summary>Is the local player inside a Stash-mode inventory screen right now?
        /// Best-effort reflection (Campaign.Current.InventoryManager.InventoryLogic); any
        /// unreadable link means "not open".</summary>
        private static bool IsLocalStashScreenOpen()
        {
            try
            {
                object manager = AccessTools.Property(typeof(Campaign), "InventoryManager")?.GetValue(Campaign.Current);
                object logic = manager != null ? AccessTools.Property(manager.GetType(), "InventoryLogic")?.GetValue(manager) : null;
                if (logic is InventoryLogic il && _inventoryModeField != null)
                {
                    object mode = _inventoryModeField.GetValue(il);
                    return mode != null && Convert.ToInt32(mode) == StashMode;
                }
            }
            catch
            {
            }
            return false;
        }

        // ---- send by reflection (never compile against BT) -----------------------------------

        private static bool Send(byte[] framed, bool isHost)
        {
            if (framed == null)
            {
                return false;
            }
            try
            {
                Type session = AccessTools.TypeByName("BannerlordTogether.CoopSession");
                if (session == null)
                {
                    return false;
                }
                if (isHost)
                {
                    object server = AccessTools.Property(session, "Server")?.GetValue(null);
                    MethodInfo send = server != null
                        ? AccessTools.Method(server.GetType(), "BroadcastRawReliableOrdered", new[] { typeof(byte[]) }) : null;
                    if (send == null)
                    {
                        return false;
                    }
                    send.Invoke(server, new object[] { framed });
                    return true;
                }
                object client = AccessTools.Property(session, "Client")?.GetValue(null);
                MethodInfo clientSend = client != null
                    ? AccessTools.Method(client.GetType(), "SendRaw", new[] { typeof(byte[]) }) : null;
                if (clientSend == null)
                {
                    return false;
                }
                clientSend.Invoke(client, new object[] { framed });
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("[STASH-SYNC] send reflection error: " + ex.Message);
                return false;
            }
        }

        // ---- self-test: wire pipeline + cross-feature discrimination -------------------------

        private static SelfHealing.TestResult LoopbackSelfTest()
        {
            try
            {
                var payload = new StashPayloadData
                {
                    SettlementStringId = "town_probe",
                    Entries =
                    {
                        new StashPayloadData.Entry { ItemStringId = "itm_a", ModifierStringId = "", Count = 3 },
                        new StashPayloadData.Entry { ItemStringId = "itm_b", ModifierStringId = "mod_x", Count = -1 }
                    }
                };
                byte[] framed = StashWireFraming.Frame(payload);
                byte[] birthFramed = PregnancySync.BirthWireFraming.Frame(new PregnancySync.BirthPayloadData { MotherStringId = "hero_probe" });
                bool recognizedOnly = StashWireFraming.IsOurPacket(framed)
                    && !StashWireFraming.IsOurPacket(birthFramed)                       // a birth packet must not read as stash
                    && !PregnancySync.BirthWireFraming.IsOurPacket(framed)               // a stash packet must not read as birth
                    && !StashWireFraming.IsOurPacket(new byte[] { 13, 0, 0, 0, 0 });     // a real BT packet must not match
                StashPayloadData parsed = StashWireFraming.TryUnframe(framed);
                bool fieldsMatch = parsed != null
                    && parsed.SettlementStringId == payload.SettlementStringId
                    && parsed.Entries.Count == 2
                    && parsed.Entries[0].ValueEquals(payload.Entries[0])
                    && parsed.Entries[1].ValueEquals(payload.Entries[1]);
                bool pass = recognizedOnly && fieldsMatch;
                return SelfHealing.TestResult.Of("stash-sync.loopback", pass,
                    pass ? "payload survived serialize->frame->receive-parse; birth/stash/BT packets discriminate cleanly"
                         : "recognizedOnly=" + recognizedOnly + " fieldsMatch=" + fieldsMatch);
            }
            catch (Exception ex)
            {
                return SelfHealing.TestResult.Of("stash-sync.loopback", false, "threw: " + ex.Message);
            }
        }
    }
}
