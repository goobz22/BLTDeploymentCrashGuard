using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BLTDeploymentCrashGuard.PregnancySync
{
    /// <summary>
    /// Co-op pregnancy/birth sync (SPEC docs/SPEC-pregnancy-coop-sync.md). BannerlordTogether
    /// disables pregnancy for the client and never replicates births, so a client's family never
    /// grows and a host's children never appear on the client. Host-authoritative fix:
    ///
    ///  HOST: hook CampaignEvents.OnGivenBirthEvent, serialize each newborn's identity, frame it
    ///        (BirthWireFraming — leading byte 0, the one free PacketType slot, + magic) and
    ///        broadcast over BT's channel (CoopSession.Server.BroadcastRawReliableOrdered, by
    ///        reflection so we never compile against BT).
    ///  CLIENT: Harmony-prefix BT's ShouldAcceptIncomingPacket (base + CoopServer override); if the
    ///        bytes are ours, QUEUE the payload (the hook runs on BT's network thread) and let the
    ///        main-thread Tick reconstruct the child, then return false so BT never processes it.
    ///        The child's id/gender/name/appearance are forced from the host; clan, parents and
    ///        birthday follow deterministically from DeliverOffSpring(mother, father) on both sides.
    ///
    /// Everything is gated on config pregnancySync AND an active BT session; inert otherwise.
    /// Default ON ("pregnancySync": true in Harness/GuardConfig.cs DefaultJson); the two-machine hop
    /// is the only part no solo test can cover. The wire format + framing + no-BT-collision are proven headless
    /// (tests/BirthPayloadTest, 24/24); the in-game loopback self-test proves a REAL hero's
    /// identity survives serialize -> frame -> receive-path parse field-for-field.
    /// </summary>
    internal static class PregnancySyncGuard
    {
        private static bool _enabled;
        private static bool _reconstructing; // guard: ignore births we create during reconstruction

        // Reconstruction is enqueued from BT's network thread (the receive hook) and drained on the
        // main game thread (Tick) — HeroCreator/MBObjectManager must never run off the game thread.
        private static readonly object QueueLock = new object();
        private static readonly Queue<BirthPayloadData> PendingBirths = new Queue<BirthPayloadData>();

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _enabled = GuardConfig.Bool("pregnancySync", true);
                // The self-test proves the wiring whether or not the feature is enabled.
                SelfHealing.RegisterTest(LoopbackSelfTest);
                // Conception visibility is diagnostic, not sync — installed regardless of
                // the sync flag so "did the waiting-at-the-castle roll happen?" is always
                // answerable from the log (operator ask 2026-08-30).
                ApplyConceptionVisibility(harmony);
                if (!_enabled)
                {
                    Diag.Report("pregnancy-sync", true, "disabled by config");
                    return;
                }
                // Harmony receive-hook is pure patching — safe at load. The host-side campaign-event
                // subscription is NOT done here: CampaignEvents resolves through Campaign.Current,
                // which is null at module load and is per-campaign, so it must be (re)wired at
                // game-start (see OnGameStart) — not once per payload generation.
                bool receiveHooked = HookReceive(harmony);
                Log.Info("[PREG-SYNC] receive hook installed (" + receiveHooked + "); host birth listener wires at game-start");
                Diag.Report("pregnancy-sync", receiveHooked, receiveHooked ? "" : "BT receive method not found");
                // A payload hot-reload mid-campaign never sees OnGameStart again, so subscribe now if
                // a campaign is already running (no-op on a fresh launch, where Campaign.Current is null).
                Subscribe("payload apply — a campaign is already running");
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] apply failed: " + ex.Message);
                Diag.Report("pregnancy-sync", false, ex.Message);
            }
        }

        // A stable listener owner object for the campaign event subscription. It is also published
        // to the harness bag so the NEXT payload generation can remove this generation's listener:
        // Harmony's UnpatchAll never touches campaign event listeners, and a payload static dies
        // with its generation, so without the bag a reload left the old listener attached forever
        // (HOTRELOAD.md § Trade-offs — closed 2026-09-04).
        private static readonly object Sentinel = new object();
        private const string ListenerOwnerKey = "pregnancy-sync.listener-owner";
        private static Campaign _subscribedCampaign;

        /// <summary>Wire the host birth listener per-campaign (CampaignEvents is per-Campaign and
        /// null at module load). Idempotent; re-subscribes when a new campaign is loaded.</summary>
        internal static void OnGameStart()
        {
            Subscribe("game-start");
        }

        private static void Subscribe(string when)
        {
            try
            {
                if (!_enabled || Campaign.Current == null || ReferenceEquals(_subscribedCampaign, Campaign.Current))
                {
                    return;
                }
                ISharedState shared = PayloadEntry.Shared;
                object previousOwner = shared != null ? shared.GetObject(ListenerOwnerKey) : null;
                if (previousOwner != null && !ReferenceEquals(previousOwner, Sentinel))
                {
                    try
                    {
                        CampaignEvents.OnGivenBirthEvent.ClearListeners(previousOwner);
                        Log.Info("[PREG-SYNC] removed the previous payload generation's birth listener");
                    }
                    catch (Exception exClear)
                    {
                        Log.Info("[PREG-SYNC] could not remove the previous generation's birth listener: " + exClear.Message + " — births may be broadcast twice until restart");
                    }
                }
                CampaignEvents.OnGivenBirthEvent.AddNonSerializedListener(Sentinel, OnGivenBirth);
                if (shared != null)
                {
                    shared.Set(ListenerOwnerKey, Sentinel);
                }
                _subscribedCampaign = Campaign.Current;
                Log.Info("[PREG-SYNC] host birth listener subscribed for this campaign (" + when + ")");
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] subscribe failed (" + when + "): " + ex.Message);
            }
        }

        /// <summary>Drain reconstructions queued from the network thread, on the MAIN game thread.</summary>
        internal static void Tick()
        {
            if (!_enabled)
            {
                return;
            }
            BirthPayloadData next;
            while (true)
            {
                lock (QueueLock)
                {
                    if (PendingBirths.Count == 0)
                    {
                        return;
                    }
                    next = PendingBirths.Dequeue();
                }
                try
                {
                    // Runs on the main game tick — a throw here (bad mother lookup, engine edge)
                    // must never escape onto the game loop. Drop this birth and keep draining.
                    ReconstructChildren(next);
                }
                catch (Exception ex)
                {
                    Log.Info("[PREG-SYNC] reconstruct drain error, dropped one birth: " + ex.Message);
                }
            }
        }

        /// <summary>Make conception observable (verified against the installed build's IL,
        /// 2026-08-30): the daily roll happens in PregnancyCampaignBehavior.RefreshSpouseVisit
        /// ONLY when CheckAreNearby(hero, spouse) passes — same settlement (waiting inside a
        /// castle counts: the party's CurrentSettlement is that castle) or same party; other
        /// clans than the player's also pass a 20% abstract roll. Two observers:
        ///  - always-on postfix on MakePregnantAction.Apply — a conception is rare and worth a
        ///    log line, plus an on-screen note for the player's own clan;
        ///  - tracing-gated postfix on CheckAreNearby for player-clan heroes, so "did waiting
        ///    at the castle count as being with my wife?" is answerable from the log.</summary>
        private static void ApplyConceptionVisibility(Harmony harmony)
        {
            try
            {
                MethodInfo conceive = AccessTools.Method(typeof(MakePregnantAction), "Apply");
                if (conceive != null)
                {
                    harmony.Patch(conceive, null, new HarmonyMethod(typeof(PregnancySyncGuard), nameof(ConceptionPostfix)));
                }
                if (GuardConfig.Bool("tracing", false))
                {
                    Type behavior = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.PregnancyCampaignBehavior");
                    MethodInfo nearby = behavior != null ? AccessTools.Method(behavior, "CheckAreNearby") : null;
                    if (nearby != null)
                    {
                        harmony.Patch(nearby, null, new HarmonyMethod(typeof(PregnancySyncGuard), nameof(NearbyCheckPostfix)));
                        Log.Info("[PREG] tracing: nearby-check visibility active (logs the player clan's daily spouse-proximity checks)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[PREG] conception visibility not installed: " + ex.Message);
            }
        }

        private static void ConceptionPostfix(Hero hero)
        {
            try
            {
                if (hero == null)
                {
                    return;
                }
                Log.Info("[PREG] conception: " + Safe(hero) + " is now pregnant (clan " +
                         (hero.Clan != null ? hero.Clan.StringId : "none") + ")");
                if (hero.Clan != null && Hero.MainHero != null && hero.Clan == Hero.MainHero.Clan)
                {
                    Log.Screen(Safe(hero) + " is pregnant");
                }
            }
            catch
            {
            }
        }

        private static void NearbyCheckPostfix(Hero hero, Hero spouse, bool __result)
        {
            try
            {
                if (hero == null || Hero.MainHero == null || hero.Clan != Hero.MainHero.Clan)
                {
                    return; // only the player clan's checks — the AI world would flood the log
                }
                Log.Info("[PREG] nearby-check " + Safe(hero) + " & " + Safe(spouse) + ": " +
                         (__result ? "TOGETHER — daily conception roll happens" : "apart, no roll") +
                         " (hero@" + Place(hero) + ", spouse@" + Place(spouse) + ")");
            }
            catch
            {
            }
        }

        private static string Place(Hero hero)
        {
            try
            {
                if (hero == null)
                {
                    return "?";
                }
                if (hero.CurrentSettlement != null)
                {
                    return hero.CurrentSettlement.Name.ToString();
                }
                if (hero.PartyBelongedTo != null)
                {
                    return "party " + hero.PartyBelongedTo.StringId;
                }
                return "nowhere";
            }
            catch
            {
                return "?";
            }
        }

        private static bool HookReceive(Harmony harmony)
        {
            bool any = false;
            foreach (string typeName in new[] { "BannerlordTogether.Network.CoopNetworkBase", "BannerlordTogether.Network.CoopServer", "BannerlordTogether.CoopNetworkBase", "BannerlordTogether.CoopServer" })
            {
                Type type = AccessTools.TypeByName(typeName);
                MethodInfo method = type != null ? AccessTools.Method(type, "ShouldAcceptIncomingPacket") : null;
                if (method != null)
                {
                    harmony.Patch(method, new HarmonyMethod(typeof(PregnancySyncGuard), nameof(ShouldAcceptIncomingPacketPrefix)));
                    any = true;
                }
            }
            return any;
        }

        // ---- HOST: broadcast a birth -------------------------------------------------------

        private static void OnGivenBirth(Hero mother, List<Hero> aliveChildren, int stillbornCount)
        {
            try
            {
                if (!_enabled || _reconstructing)
                {
                    return;
                }
                ISharedState shared = PayloadEntry.Shared;
                object owner = shared != null ? shared.GetObject(ListenerOwnerKey) : null;
                if (owner != null && !ReferenceEquals(owner, Sentinel))
                {
                    return; // a newer payload generation owns the listener now; this one is retired
                }
                if (PeerDetection.ReadCoopStaticBool("IsHost") != true)
                {
                    return; // only the host is authoritative for births
                }
                if (PeerDetection.AnyRemotePeerConnected() != true)
                {
                    return; // no client to inform
                }
                BirthPayloadData payload = BuildPayload(mother, aliveChildren, stillbornCount);
                if (payload == null || payload.Children.Count == 0)
                {
                    return;
                }
                byte[] framed = BirthWireFraming.Frame(payload);
                if (!Broadcast(framed))
                {
                    Log.Info("[PREG-SYNC] host could not broadcast birth (send reflection failed) — client will miss this child");
                    return;
                }
                SelfHealing.RecordFire("pregnancy-sync");
                Log.Info("[PREG-SYNC] host broadcast birth: mother=" + Safe(mother) + " children=" + payload.Children.Count);
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] OnGivenBirth error: " + ex.Message);
            }
        }

        internal static BirthPayloadData BuildPayload(Hero mother, List<Hero> aliveChildren, int stillbornCount)
        {
            if (mother == null || aliveChildren == null)
            {
                return null;
            }
            var payload = new BirthPayloadData
            {
                MotherStringId = IdOf(mother),
                StillbornCount = stillbornCount,
                Children = new List<BirthPayloadData.ChildIdentity>()
            };
            foreach (Hero child in aliveChildren)
            {
                if (child == null)
                {
                    continue;
                }
                payload.Children.Add(ChildFrom(child));
            }
            return payload;
        }

        internal static BirthPayloadData.ChildIdentity ChildFrom(Hero child)
        {
            return new BirthPayloadData.ChildIdentity
            {
                StringId = IdOf(child),
                IsFemale = child.IsFemale,
                FirstName = child.FirstName != null ? child.FirstName.ToString() : "",
                BodyPropertiesXml = child.BodyProperties.ToString(),
                FatherStringId = IdOf(child.Father)
            };
        }

        // ---- CLIENT: intercept and reconstruct ---------------------------------------------

        private static bool ShouldAcceptIncomingPacketPrefix(byte[] data, ref bool __result)
        {
            try
            {
                if (!_enabled || !BirthWireFraming.IsOurPacket(data))
                {
                    return true; // not ours — let BT decide normally
                }
                BirthPayloadData payload = BirthWireFraming.TryUnframe(data);
                if (payload != null)
                {
                    // This runs on BT's LiteNetLib network thread. Parsing bytes is thread-safe;
                    // hero creation is NOT — queue it for the main-thread Tick to reconstruct.
                    lock (QueueLock)
                    {
                        PendingBirths.Enqueue(payload);
                    }
                }
                else
                {
                    Log.Info("[PREG-SYNC] received a malformed birth packet — dropped");
                }
                __result = false; // consume: BT must not enqueue/dispatch our packet
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] receive error: " + ex.Message);
                return true; // on any doubt, let BT handle it (our marker means BT no-ops anyway)
            }
        }

        internal static void ReconstructChildren(BirthPayloadData payload)
        {
            if (Campaign.Current == null || payload == null)
            {
                return;
            }
            Hero mother = FindHero(payload.MotherStringId);
            foreach (BirthPayloadData.ChildIdentity identity in payload.Children)
            {
                try
                {
                    if (FindHero(identity.StringId) != null)
                    {
                        continue; // already present (idempotent — re-sent packet or shared base save)
                    }
                    Hero father = FindHero(identity.FatherStringId);
                    if (mother == null || father == null)
                    {
                        Log.Info("[PREG-SYNC] cannot reconstruct child " + identity.StringId + " — parent not resolved (mother=" + (mother != null) + " father=" + (father != null) + ")");
                        continue;
                    }
                    _reconstructing = true;
                    try
                    {
                        Hero child = HeroCreator.DeliverOffSpring(mother, father, identity.IsFemale);
                        if (child == null)
                        {
                            Log.Info("[PREG-SYNC] DeliverOffSpring returned null for " + identity.StringId);
                            continue;
                        }
                        AlignToHost(child, identity);
                        SelfHealing.RecordFire("pregnancy-sync");
                        Log.Info("[PREG-SYNC] reconstructed child " + identity.StringId + " (" + identity.FirstName + ") on client");
                        Log.Screen("a child was born in your co-op family: " + identity.FirstName);
                    }
                    finally
                    {
                        _reconstructing = false;
                    }
                }
                catch (Exception ex)
                {
                    _reconstructing = false;
                    Log.Info("[PREG-SYNC] reconstruct error for " + identity.StringId + ": " + ex.Message);
                }
            }
        }

        /// <summary>Force the reconstructed child to share the host's identity: same StringId
        /// (re-registered in MBObjectManager) and body properties + name, so every later
        /// reference resolves identically on both machines.</summary>
        private static void AlignToHost(Hero child, BirthPayloadData.ChildIdentity identity)
        {
            try
            {
                if (!string.IsNullOrEmpty(identity.BodyPropertiesXml)
                    && BodyProperties.FromString(identity.BodyPropertiesXml, out BodyProperties bodyProperties))
                {
                    child.StaticBodyProperties = bodyProperties.StaticProperties;
                }
                if (!string.IsNullOrEmpty(identity.FirstName))
                {
                    var firstName = new TaleWorlds.Localization.TextObject(identity.FirstName);
                    child.SetName(firstName, firstName);
                }
                // Re-key the object id to the host's so cross-machine references match:
                // unregister, set the StringId, re-register under the host's id.
                if (!string.IsNullOrEmpty(identity.StringId) && IdOf(child) != identity.StringId)
                {
                    MBObjectManager.Instance.UnregisterObject(child);
                    child.StringId = identity.StringId;
                    MBObjectManager.Instance.RegisterPresumedObject(child);
                }
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] align-to-host partial for " + identity.StringId + ": " + ex.Message);
            }
        }

        // ---- send by reflection (never compile against BT) ---------------------------------

        private static bool Broadcast(byte[] framed)
        {
            if (framed == null)
            {
                return false;
            }
            try
            {
                Type session = AccessTools.TypeByName("BannerlordTogether.CoopSession");
                object server = session != null ? AccessTools.Property(session, "Server")?.GetValue(null) : null;
                if (server == null)
                {
                    return false;
                }
                MethodInfo send = AccessTools.Method(server.GetType(), "BroadcastRawReliableOrdered", new[] { typeof(byte[]) });
                if (send == null)
                {
                    return false;
                }
                send.Invoke(server, new object[] { framed });
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("[PREG-SYNC] broadcast reflection error: " + ex.Message);
                return false;
            }
        }

        // ---- helpers ------------------------------------------------------------------------

        private static string IdOf(MBObjectBase obj)
        {
            return obj != null ? obj.StringId : "";
        }

        private static string IdOf(Hero hero)
        {
            return hero != null ? hero.StringId : "";
        }

        private static Hero FindHero(string stringId)
        {
            if (string.IsNullOrEmpty(stringId))
            {
                return null;
            }
            return Hero.FindFirst(h => h != null && h.StringId == stringId);
        }

        private static string Safe(Hero hero)
        {
            try { return hero != null && hero.Name != null ? hero.Name.ToString() : "null"; }
            catch { return "?"; }
        }

        // ---- self-test: prove a REAL hero's identity survives the pipeline ------------------

        private static SelfHealing.TestResult LoopbackSelfTest()
        {
            // Take a live hero (MainHero), serialize its identity into a birth payload AS IF it
            // were a newborn, frame it, run the exact receive-path unframe, and assert the parsed
            // identity matches the live hero field-for-field. Proves serialize + frame + collision
            // gate + parse against real engine data, with NO bogus hero created and no network.
            try
            {
                Hero probe = Hero.MainHero;
                if (probe == null)
                {
                    return SelfHealing.TestResult.Of("pregnancy-sync.loopback", true, "no MainHero yet (menu) — pipeline untested this tick, not a failure");
                }
                var payload = new BirthPayloadData
                {
                    MotherStringId = IdOf(probe),
                    Children = { ChildFrom(probe) }
                };
                byte[] framed = BirthWireFraming.Frame(payload);
                bool recognizedOnly = BirthWireFraming.IsOurPacket(framed)
                    && !BirthWireFraming.IsOurPacket(new byte[] { 13, 0, 0, 0 }); // PlayerHeroData=13 must not match
                BirthPayloadData parsed = BirthWireFraming.TryUnframe(framed);
                bool fieldsMatch = parsed != null
                    && parsed.Children.Count == 1
                    && parsed.Children[0].IdentityEquals(payload.Children[0])
                    && parsed.MotherStringId == payload.MotherStringId;
                bool pass = recognizedOnly && fieldsMatch;
                return SelfHealing.TestResult.Of("pregnancy-sync.loopback", pass,
                    pass ? "real hero identity survived serialize->frame->receive-parse; BT type not misread"
                         : "recognizedOnly=" + recognizedOnly + " fieldsMatch=" + fieldsMatch);
            }
            catch (Exception ex)
            {
                return SelfHealing.TestResult.Of("pregnancy-sync.loopback", false, "threw: " + ex.Message);
            }
        }
    }
}
