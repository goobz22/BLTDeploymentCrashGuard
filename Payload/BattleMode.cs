using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Automatic battle-mode switching.
    ///
    /// Requirement: battles must work BOTH hosting solo and in real co-op, with no
    /// manual toggling.
    ///
    ///  - Hosting alone: the co-op mod's battle pipeline strips the player side out of
    ///    missions (proven 2026-08-18: empty formations, SetupTeams NRE). Remedy: lift
    ///    all foreign Harmony patches off a fixed list of native battle/deployment/
    ///    spawn methods so those run pure vanilla. The removed patches are STASHED.
    ///  - A remote player is connected (or we are a client): restore every stashed
    ///    patch under its original owner/priority so the co-op synced-battle pipeline
    ///    is fully intact.
    ///
    /// WHEN the decision runs (all always-on, tracing or not):
    ///  - payload apply, module screen, game start, mission init (PayloadEntry), and
    ///  - the two battle chokepoints this class hooks itself: PlayerEncounter.StartBattle
    ///    and MissionState.OpenNew.
    /// The chokepoints are the ones that matter: BannerlordTogether installs its 24 battle
    /// patches AFTER our game-start decision, and the pre-mission half of them
    /// (MapEventSide.MakeReadyForMission, the troop-supplier model, Order of Battle) runs
    /// BEFORE mission init. Field evidence 2026-09-04: across every log segment the only
    /// decision that ever lifted the 24 patches was "start-battle" — which until v1.3.3
    /// lived in the tracer and therefore existed only with tracing=true. With tracing off
    /// (the default) the first solo battle of a session ran with the player side stripped.
    ///
    /// Peer detection reads the running session's public state via reflection
    /// (type/member lookup by name, values only) — runtime interop, no third-party
    /// code is read, copied, or modified. If the co-op mod is absent every step
    /// no-ops. Config override in guardconfig.json: {"battleMode":"auto"|"solo"|"coop"}
    /// (a launch-time snapshot — edit needs a restart; the legacy key
    /// "soloVanillaBattles": false maps to "coop").
    /// </summary>
    internal static class BattleMode
    {
        private const string Component = "battle-mode";
        private const string Tag = "[BATTLE-MODE]";
        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // Native methods whose foreign patches are lifted in vanilla mode. Battle-
        // mission scope only — campaign/map co-op machinery is intentionally not listed.
        private static readonly KeyValuePair<string, string[]>[] BattleTargets =
        {
            new KeyValuePair<string, string[]>("TaleWorlds.CampaignSystem.GameComponents.DefaultTroopSupplierProbabilityModel",
                new[] { "EnqueueTroopSpawnProbabilitiesAccordingToUnitSpawnPrioritization" }),
            new KeyValuePair<string, string[]>("TaleWorlds.CampaignSystem.MapEvents.MapEventSide",
                new[] { "MakeReadyForMission", "OnTroopKilled", "OnTroopWounded", "OnTroopScoreHit" }),
            new KeyValuePair<string, string[]>("TaleWorlds.CampaignSystem.CampaignBehaviors.OrderOfBattleCampaignBehavior",
                new[] { "GetFormationDataAtIndex", "SetFormationInfos" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.DefaultBattleMissionAgentSpawnLogic",
                new[] { "OnSideDeploymentOver" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.DeploymentMissionController",
                new[] { "OnMissionTick", "FinishDeployment", "SetupAIOfEnemyTeam" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.ComponentInterfaces.BattleInitializationModel",
                new[] { "CanPlayerSideDeployWithOrderOfBattle" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.BattleEndLogic",
                new[] { "MissionEnded", "OnAgentRemoved" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.BattleObserverMissionLogic",
                new[] { "OnAgentRemoved" }),
            new KeyValuePair<string, string[]>("TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle.OrderOfBattleVM",
                new[] { "Initialize", "ExecuteBeginMission", "OnDeploymentFinalized", "RefreshValues" }),
            new KeyValuePair<string, string[]>("SandBox.GameComponents.SandboxBattleInitializationModel",
                new[] { "GetAllAvailableTroopTypes" }),
            new KeyValuePair<string, string[]>("SandBox.Missions.MissionLogics.BattleAgentLogic",
                new[] { "OnAgentBuild", "CheckUpgrade", "OnAgentHit", "OnAgentRemoved" }),
        };

        private const string PlayerEncounterType = "TaleWorlds.CampaignSystem.Encounters.PlayerEncounter";
        private const string MissionStateType = "TaleWorlds.MountAndBlade.MissionState";

        private sealed class StashedPatch
        {
            public string Owner;
            public int Kind; // 0 prefix, 1 postfix, 2 finalizer, 3 transpiler
            public MethodInfo PatchMethod;
            public int Priority;
            public string[] Before;
            public string[] After;
        }

        private static readonly Dictionary<MethodBase, List<StashedPatch>> Stash = new Dictionary<MethodBase, List<StashedPatch>>();
        private static readonly HashSet<string> WarnedUnresolved = new HashSet<string>(StringComparer.Ordinal);
        private static string _configMode;
        private static bool? _lastVanilla;
        private static bool _applied;

        internal static string ConfigMode
        {
            get
            {
                if (_configMode == null)
                {
                    _configMode = ReadConfig();
                }
                return _configMode;
            }
        }

        /// <summary>Hook the two battle chokepoints (always-on) and register health + self-test.
        /// Idempotent per generation; Apply is retried from the module screen like the other guards.</summary>
        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                SelfHealing.RegisterTest(SelfTest);
                bool startBattle = PatchByName(harmony, PlayerEncounterType, "StartBattle", nameof(StartBattlePrefix)) > 0;
                bool missionOpen = PatchByName(harmony, MissionStateType, "OpenNew", nameof(MissionOpenPrefix)) > 0;
                List<string> unresolved;
                int resolvedMethods = ResolveTargets(out unresolved);
                string detail = "chokepoints StartBattle=" + startBattle + " OpenNew=" + missionOpen +
                                "; lift targets " + resolvedMethods + "/" + ExpectedTargetMethods() + " method(s)" +
                                (unresolved.Count > 0 ? "; unresolved: " + string.Join(", ", unresolved.ToArray()) : "");
                bool ok = startBattle && missionOpen && unresolved.Count == 0;
                // The chokepoints are load-bearing (without them solo battles strip the player
                // side); a missing lift target only degrades one lifted method, so it is
                // reported but not critical.
                Diag.Report(Component, ok, ok ? "" : detail, critical: !(startBattle && missionOpen));
                _applied = true;
                Log.Info(Tag + " battle chokepoints hooked — " + detail);
            }
            catch (Exception ex)
            {
                Diag.Report(Component, false, ex.Message, critical: true);
                Log.Info(Tag + " apply failed: " + ex.Message);
            }
        }

        private static void StartBattlePrefix()
        {
            DecideAndApply(PayloadEntry.Harmony, "start-battle");
        }

        private static void MissionOpenPrefix()
        {
            // Last-chance mode decision before the mission is built.
            DecideAndApply(PayloadEntry.Harmony, "mission-open");
        }

        private static int PatchByName(Harmony harmony, string typeName, string methodName, string prefixName)
        {
            int count = 0;
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.Info(Tag + " chokepoint type not found: " + typeName);
                    return 0;
                }
                foreach (MethodInfo method in type.GetMethods(AllDeclared))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(BattleMode), prefixName));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info(Tag + " could not hook " + type.Name + "." + methodName + ": " + exOne.Message);
                    }
                }
                if (count == 0)
                {
                    Log.Info(Tag + " chokepoint method not found: " + type.Name + "." + methodName);
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " hook failed for " + typeName + "." + methodName + ": " + ex.Message);
            }
            return count;
        }

        /// <summary>True for any Harmony owner belonging to this mod — the per-generation ids
        /// "bltogether.crashguard.gen{N}" (and the legacy flat id). Used to skip our own patches
        /// when lifting BannerlordTogether's.</summary>
        internal static bool IsOwnOwner(string owner)
        {
            return owner != null && owner.StartsWith("bltogether", StringComparison.Ordinal);
        }

        /// <summary>The pure decision: given the config mode and the two peer-detection reads,
        /// should battles run vanilla (true) or with BT's co-op pipeline intact (false)?
        /// FAIL TOWARD CO-OP: stripping the co-op mod's battle patches on a machine that is
        /// actually in a session sabotages the session (the partner's army never enters the
        /// authoritative battle). Vanilla engages only on a CONFIDENT "no session"; anything
        /// unreadable leaves co-op fully intact. Solo players who hit the empty-battle bug can
        /// force it with battleMode=solo.</summary>
        internal static bool WantVanilla(string mode, bool? isClient, bool? remote, out string detail)
        {
            if (mode == "solo")
            {
                detail = "config=solo";
                return true;
            }
            if (mode == "coop")
            {
                detail = "config=coop";
                return false;
            }
            if (isClient == true)
            {
                detail = "auto: we are a client in someone else's session";
                return false;
            }
            if (remote == false)
            {
                detail = "auto: confidently no session";
                return true;
            }
            detail = remote == true
                ? "auto: remote player connected"
                : "auto: state unreadable — failing safe to co-op (battleMode=solo forces vanilla)";
            return false;
        }

        internal static void DecideAndApply(Harmony harmony, string reason)
        {
            if (harmony == null)
            {
                return;
            }
            try
            {
                string mode = ConfigMode;
                bool? isClient = null;
                bool? remote = null;
                if (mode != "solo" && mode != "coop")
                {
                    isClient = PeerDetection.IsClient();
                    if (isClient != true)
                    {
                        remote = PeerDetection.AnyRemotePeerConnected();
                    }
                }
                string detail;
                bool wantVanilla = WantVanilla(mode, isClient, remote, out detail);
                if (wantVanilla)
                {
                    EnsureVanilla(harmony, reason, detail);
                }
                else
                {
                    EnsureCoop(reason, detail);
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " decide failed (" + reason + "): " + ex);
            }
        }

        private static void EnsureVanilla(Harmony harmony, string reason, string detail)
        {
            int removed = 0;
            foreach (MethodInfo method in EnumerateTargets())
            {
                removed += StashAndRemoveForeign(harmony, method);
            }
            if (removed > 0 || _lastVanilla != true)
            {
                _lastVanilla = true;
                Log.Info(Tag + " VANILLA battles active (" + detail + ", " + reason + ") — removed " + removed + " foreign patch(es)");
                if (removed > 0)
                {
                    SelfHealing.RecordFire(Component);
                    Log.Screen("battles set to native/vanilla (" + detail + ")");
                }
            }
        }

        private static void EnsureCoop(string reason, string detail)
        {
            int restored = 0;
            foreach (KeyValuePair<MethodBase, List<StashedPatch>> entry in Stash)
            {
                Patches current = Harmony.GetPatchInfo(entry.Key);
                foreach (StashedPatch stashed in entry.Value)
                {
                    if (IsPresent(current, stashed))
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyMethod patch = new HarmonyMethod(stashed.PatchMethod)
                        {
                            priority = stashed.Priority,
                            before = stashed.Before,
                            after = stashed.After
                        };
                        Harmony ownerHarmony = new Harmony(stashed.Owner);
                        switch (stashed.Kind)
                        {
                            case 0: ownerHarmony.Patch(entry.Key, patch); break;
                            case 1: ownerHarmony.Patch(entry.Key, null, patch); break;
                            case 2: ownerHarmony.Patch(entry.Key, null, null, null, patch); break;
                            case 3: ownerHarmony.Patch(entry.Key, null, null, patch); break;
                        }
                        restored++;
                        current = Harmony.GetPatchInfo(entry.Key);
                    }
                    catch (Exception ex)
                    {
                        Log.Info(Tag + " failed to restore patch on " + entry.Key.Name + " (owner=" + stashed.Owner + "): " + ex.Message);
                    }
                }
            }
            if (restored > 0 || _lastVanilla != false)
            {
                _lastVanilla = false;
                Log.Info(Tag + " CO-OP battles active (" + detail + ", " + reason + ") — restored " + restored + " stashed patch(es)");
                if (restored > 0)
                {
                    SelfHealing.RecordFire(Component);
                    Log.Screen("co-op battle sync restored (" + detail + ")");
                }
            }
        }

        private static IEnumerable<MethodInfo> EnumerateTargets()
        {
            foreach (KeyValuePair<string, string[]> target in BattleTargets)
            {
                Type type = AccessTools.TypeByName(target.Key);
                if (type == null)
                {
                    // Never silent: an unresolved target means BT's patch on it CANNOT be
                    // lifted, i.e. a game rename just re-exposed part of the solo bug.
                    if (WarnedUnresolved.Add(target.Key))
                    {
                        Log.Info(Tag + " lift target type not found: " + target.Key + " — its BT patches cannot be lifted (game update?)");
                    }
                    continue;
                }
                foreach (string methodName in target.Value)
                {
                    bool found = false;
                    foreach (MethodInfo method in type.GetMethods(AllDeclared))
                    {
                        if (method.Name == methodName && !method.IsAbstract)
                        {
                            found = true;
                            yield return method;
                        }
                    }
                    if (!found && WarnedUnresolved.Add(target.Key + "." + methodName))
                    {
                        Log.Info(Tag + " lift target method not found: " + type.Name + "." + methodName + " (game update?)");
                    }
                }
            }
        }

        /// <summary>Resolve every lift target by name; returns the resolved method count and the
        /// list of unresolved "Type" / "Type.Method" names. Used by health and the self-test.</summary>
        private static int ResolveTargets(out List<string> unresolved)
        {
            unresolved = new List<string>();
            int resolved = 0;
            foreach (KeyValuePair<string, string[]> target in BattleTargets)
            {
                Type type = AccessTools.TypeByName(target.Key);
                if (type == null)
                {
                    unresolved.Add(ShortName(target.Key));
                    continue;
                }
                foreach (string methodName in target.Value)
                {
                    bool found = false;
                    foreach (MethodInfo method in type.GetMethods(AllDeclared))
                    {
                        if (method.Name == methodName && !method.IsAbstract)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found)
                    {
                        resolved++;
                    }
                    else
                    {
                        unresolved.Add(type.Name + "." + methodName);
                    }
                }
            }
            return resolved;
        }

        internal static int ExpectedTargetMethods()
        {
            int n = 0;
            foreach (KeyValuePair<string, string[]> target in BattleTargets)
            {
                n += target.Value.Length;
            }
            return n;
        }

        private static string ShortName(string fullTypeName)
        {
            int dot = fullTypeName.LastIndexOf('.');
            return dot >= 0 ? fullTypeName.Substring(dot + 1) : fullTypeName;
        }

        private static int StashAndRemoveForeign(Harmony harmony, MethodInfo method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return 0;
            }
            HashSet<string> foreignOwners = new HashSet<string>(StringComparer.Ordinal);
            StashKind(method, info.Prefixes, 0, foreignOwners);
            StashKind(method, info.Postfixes, 1, foreignOwners);
            StashKind(method, info.Finalizers, 2, foreignOwners);
            StashKind(method, info.Transpilers, 3, foreignOwners);
            int removed = 0;
            foreach (string owner in foreignOwners)
            {
                try
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, owner);
                    removed++;
                    Log.Info(Tag + " lifted " + (method.DeclaringType != null ? method.DeclaringType.Name : "?") + "." + method.Name + " (owner=" + owner + ")");
                }
                catch (Exception ex)
                {
                    Log.Info(Tag + " failed to lift " + method.Name + " owner=" + owner + ": " + ex.Message);
                }
            }
            return removed;
        }

        private static void StashKind(MethodBase method, IEnumerable<Patch> patches, int kind, HashSet<string> foreignOwners)
        {
            if (patches == null)
            {
                return;
            }
            foreach (Patch patch in patches)
            {
                if (patch == null || string.IsNullOrEmpty(patch.owner) || IsOwnOwner(patch.owner))
                {
                    continue;
                }
                foreignOwners.Add(patch.owner);
                List<StashedPatch> list;
                if (!Stash.TryGetValue(method, out list))
                {
                    list = new List<StashedPatch>();
                    Stash[method] = list;
                }
                bool known = false;
                foreach (StashedPatch existing in list)
                {
                    if (existing.Kind == kind && existing.Owner == patch.owner && existing.PatchMethod == patch.PatchMethod)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    list.Add(new StashedPatch
                    {
                        Owner = patch.owner,
                        Kind = kind,
                        PatchMethod = patch.PatchMethod,
                        Priority = patch.priority,
                        Before = patch.before,
                        After = patch.after
                    });
                }
            }
        }

        private static bool IsPresent(Patches info, StashedPatch stashed)
        {
            if (info == null)
            {
                return false;
            }
            IEnumerable<Patch> patches;
            switch (stashed.Kind)
            {
                case 0: patches = info.Prefixes; break;
                case 1: patches = info.Postfixes; break;
                case 2: patches = info.Finalizers; break;
                default: patches = info.Transpilers; break;
            }
            if (patches == null)
            {
                return false;
            }
            foreach (Patch patch in patches)
            {
                if (patch != null && patch.owner == stashed.Owner && patch.PatchMethod == stashed.PatchMethod)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Parse the mode out of guardconfig.json text: "battleMode" wins; the legacy
        /// v2.0 key "soloVanillaBattles": false maps to "coop"; anything else is "auto".</summary>
        internal static string ParseMode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "auto";
            }
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, "\"battleMode\"\\s*:\\s*\"(auto|solo|coop)\"");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "\"soloVanillaBattles\"\\s*:\\s*false"))
            {
                return "coop";
            }
            return "auto";
        }

        private static string ReadConfig()
        {
            try
            {
                // GuardConfig materializes the fully documented default file on the harness's
                // first read (before any payload code runs). This class must NEVER write its own
                // stub: an earlier two-key writer could leave a player with an undocumented file
                // if the harness write had failed.
                string configPath = GuardConfig.Path;
                string text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
                string mode = ParseMode(text);
                Log.Info(Tag + " config: battleMode=" + mode +
                         (text.Length == 0 ? " (no guardconfig.json yet — defaults)" : "") +
                         (mode == "coop" && text.IndexOf("soloVanillaBattles", StringComparison.Ordinal) >= 0 && text.IndexOf("\"battleMode\"", StringComparison.Ordinal) < 0 ? " (legacy soloVanillaBattles=false)" : ""));
                return mode;
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " config read failed, defaulting to auto: " + ex.Message);
                return "auto";
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            List<string> unresolved;
            int resolved = ResolveTargets(out unresolved);
            bool targets = unresolved.Count == 0 && resolved == ExpectedTargetMethods();
            Type playerEncounter = AccessTools.TypeByName(PlayerEncounterType);
            Type missionState = AccessTools.TypeByName(MissionStateType);
            bool chokepoints = playerEncounter != null && missionState != null &&
                               HasMethod(playerEncounter, "StartBattle") && HasMethod(missionState, "OpenNew");
            string d;
            bool decisions =
                WantVanilla("solo", true, true, out d) &&
                !WantVanilla("coop", false, false, out d) &&
                !WantVanilla("auto", true, null, out d) &&
                WantVanilla("auto", false, false, out d) &&
                !WantVanilla("auto", false, true, out d) &&
                !WantVanilla("auto", false, null, out d) &&
                !WantVanilla("auto", null, null, out d);
            bool owners = IsOwnOwner("bltogether.crashguard.gen3") && IsOwnOwner("bltogether.crashguard") &&
                          !IsOwnOwner("BannerlordTogether.mod") && !IsOwnOwner(null) && !IsOwnOwner("");
            bool config = ParseMode("{\"battleMode\": \"solo\"}") == "solo" &&
                          ParseMode("{\"battleMode\":\"coop\"}") == "coop" &&
                          ParseMode("{\"soloVanillaBattles\": false}") == "coop" &&
                          ParseMode("{\"soloVanillaBattles\": true}") == "auto" &&
                          ParseMode("{}") == "auto" && ParseMode(null) == "auto";
            bool pass = targets && chokepoints && decisions && owners && config;
            return SelfHealing.TestResult.Of("battle-mode.contract", pass,
                pass ? "all " + resolved + " lift targets + both chokepoints re-resolved; decision table, owner filter and config parser verified"
                     : "targets=" + targets + " (" + resolved + "/" + ExpectedTargetMethods() + (unresolved.Count > 0 ? ", missing " + string.Join(", ", unresolved.ToArray()) : "") + ")" +
                       " chokepoints=" + chokepoints + " decisions=" + decisions + " owners=" + owners + " config=" + config);
        }

        private static bool HasMethod(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods(AllDeclared))
            {
                if (method.Name == name && !method.IsAbstract)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Reads the co-op session's public runtime state (is a session live, are remote
    /// peers connected) by name via reflection. Values only; nulls mean "unknown".
    /// </summary>
    internal static class PeerDetection
    {
        private static bool _searched;
        private static Type _sessionType;
        private static int _lastActivityTick;

        /// <summary>
        /// Liveness fail-safe: reflection reads of the session state proved unreliable
        /// (2026-08-19 20:27 — "no remote player" while packets arrived every 2s,
        /// which desynced the two players' speeds). The co-op mod's own packet
        /// handlers firing IS proof of a live session, so traced calls stamp this.
        /// </summary>
        internal static void NoteCoopActivity()
        {
            _lastActivityTick = Environment.TickCount;
        }

        private static bool RecentCoopActivity()
        {
            int last = _lastActivityTick;
            if (last == 0)
            {
                return false;
            }
            int now = Environment.TickCount;
            return now - last < 15000 && now >= last;
        }

        internal static string Snapshot()
        {
            try
            {
                Type type = SessionType;
                if (type == null)
                {
                    return "sessionType=missing";
                }
                object isClient = ReadStaticMember(type, "IsClient");
                object isHost = ReadStaticMember(type, "IsHost");
                object server = ReadStaticMember(type, "Server");
                string peers = "n/a";
                if (server != null)
                {
                    foreach (string memberName in new[] { "GameplayPeerIds", "ConnectedPeerIds" })
                    {
                        IEnumerable ids = ReadInstanceMember(server, memberName) as IEnumerable;
                        if (ids != null)
                        {
                            int peerCount = 0;
                            foreach (object unused in ids)
                            {
                                peerCount++;
                            }
                            peers = memberName + "=" + peerCount;
                            break;
                        }
                    }
                }
                return "isClient=" + (isClient ?? "?") + " isHost=" + (isHost ?? "?") + " server=" + (server == null ? "null" : "set") + " " + peers + " recentPackets=" + RecentCoopActivity();
            }
            catch (Exception ex)
            {
                return "snapshot failed: " + ex.Message;
            }
        }

        /// <summary>True when a BannerlordTogether assembly is loaded at all — distinguishes
        /// "BT absent (guard legitimately inert)" from "BT present but a type was renamed
        /// (guard degraded)" for health reporting.</summary>
        internal static bool IsCoopAssemblyLoaded()
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "BannerlordTogether")
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>Find a type by simple name in the co-op mod's assembly (null if absent).</summary>
        internal static Type FindCoopType(string simpleName)
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "BannerlordTogether")
                    {
                        continue;
                    }
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException loadEx)
                    {
                        types = loadEx.Types;
                    }
                    foreach (Type type in types)
                    {
                        if (type != null && type.Name == simpleName)
                        {
                            return type;
                        }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Info("[PEER-DETECT] type lookup failed for " + simpleName + ": " + ex.Message);
            }
            return null;
        }

        private static Type SessionType
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _sessionType = FindCoopType("CoopSession");
                }
                return _sessionType;
            }
        }

        internal static bool? IsClient()
        {
            return ReadStaticBool("IsClient");
        }

        internal static bool? AnyRemotePeerConnected()
        {
            if (RecentCoopActivity())
            {
                return true; // packets are arriving — a live session regardless of what reflection says
            }
            Type type = SessionType;
            if (type == null)
            {
                return null; // co-op mod absent or unreadable
            }
            if (ReadStaticBool("IsClient") == true)
            {
                return true;
            }
            bool? isHost = ReadStaticBool("IsHost");
            bool? isClient = ReadStaticBool("IsClient");
            object server = ReadStaticMember(type, "Server");
            if (server == null)
            {
                // Confident "no session" ONLY when both role flags read false. A null
                // Server with unreadable roles previously returned false and caused a
                // mid-session false-alone (2026-08-19 20:27) — that must be UNKNOWN.
                if (isHost == false && isClient == false)
                {
                    return false;
                }
                return null;
            }
            bool sawCollection = false;
            foreach (string memberName in new[] { "GameplayPeerIds", "ConnectedPeerIds" })
            {
                IEnumerable ids = ReadInstanceMember(server, memberName) as IEnumerable;
                if (ids == null)
                {
                    continue;
                }
                sawCollection = true;
                foreach (object unused in ids)
                {
                    return true;
                }
            }
            return sawCollection ? (bool?)false : null;
        }

        /// <summary>Shared read of a CoopSession static bool (IsHost/IsClient/IsActive) for the
        /// other guards — same resolution + caching as battle-mode decisions.</summary>
        internal static bool? ReadCoopStaticBool(string name)
        {
            return ReadStaticBool(name);
        }

        /// <summary>A static string on the co-op session (e.g. the remote player's hero id), or null.</summary>
        internal static string ReadCoopStaticString(string name)
        {
            Type type = SessionType;
            if (type == null)
            {
                return null;
            }
            return ReadStaticMember(type, name) as string;
        }

        private static bool? ReadStaticBool(string name)
        {
            Type type = SessionType;
            if (type == null)
            {
                return null;
            }
            object value = ReadStaticMember(type, name);
            return value is bool ? (bool?)(bool)value : null;
        }

        private static object ReadStaticMember(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null)
                {
                    return property.GetValue(null);
                }
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return field != null ? field.GetValue(null) : null;
            }
            catch
            {
                return null;
            }
        }

        private static object ReadInstanceMember(object instance, string name)
        {
            try
            {
                Type type = instance.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null)
                {
                    return property.GetValue(instance);
                }
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return field != null ? field.GetValue(instance) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
