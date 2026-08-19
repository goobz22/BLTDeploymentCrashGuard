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
    /// The decision runs at game start and again at every battle chokepoint
    /// (settlement encounter, PlayerEncounter.StartBattle, MissionState.OpenNew), so
    /// a friend joining or leaving mid-session flips the mode before the next battle.
    ///
    /// Peer detection reads the running session's public state via reflection
    /// (type/member lookup by name, values only) — runtime interop, no third-party
    /// code is read, copied, or modified. If the co-op mod is absent every step
    /// no-ops. Config override in guardconfig.json: {"battleMode":"auto"|"solo"|"coop"}.
    /// </summary>
    internal static class BattleMode
    {
        private const string ConfigFileName = "guardconfig.json";

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
        private static string _configMode;
        private static bool? _lastVanilla;

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

        internal static void DecideAndApply(Harmony harmony, string reason)
        {
            if (harmony == null)
            {
                return;
            }
            try
            {
                string mode = ConfigMode;
                bool wantVanilla;
                string detail;
                if (mode == "solo")
                {
                    wantVanilla = true;
                    detail = "config=solo";
                }
                else if (mode == "coop")
                {
                    wantVanilla = false;
                    detail = "config=coop";
                }
                else if (PeerDetection.IsClient() == true)
                {
                    wantVanilla = false;
                    detail = "auto: we are a client in someone else's session";
                }
                else
                {
                    bool? remote = PeerDetection.AnyRemotePeerConnected();
                    if (remote == true)
                    {
                        wantVanilla = false;
                        detail = "auto: remote player connected";
                    }
                    else if (remote == false)
                    {
                        wantVanilla = true;
                        detail = "auto: hosting alone";
                    }
                    else
                    {
                        wantVanilla = true;
                        detail = "auto: co-op state unreadable — assuming alone (guardconfig battleMode=coop overrides)";
                    }
                }

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
                Log.Info("[BATTLE-MODE] decide failed (" + reason + "): " + ex);
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
                Log.Info("[BATTLE-MODE] VANILLA battles active (" + detail + ", " + reason + ") — removed " + removed + " foreign patch(es)");
                if (removed > 0)
                {
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
                        Log.Info("[BATTLE-MODE] failed to restore patch on " + entry.Key.Name + " (owner=" + stashed.Owner + "): " + ex.Message);
                    }
                }
            }
            if (restored > 0 || _lastVanilla != false)
            {
                _lastVanilla = false;
                Log.Info("[BATTLE-MODE] CO-OP battles active (" + detail + ", " + reason + ") — restored " + restored + " stashed patch(es)");
                if (restored > 0)
                {
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
                    continue;
                }
                foreach (string methodName in target.Value)
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name == methodName && !method.IsAbstract)
                        {
                            yield return method;
                        }
                    }
                }
            }
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
                    Log.Info("[BATTLE-MODE] lifted " + (method.DeclaringType != null ? method.DeclaringType.Name : "?") + "." + method.Name + " (owner=" + owner + ")");
                }
                catch (Exception ex)
                {
                    Log.Info("[BATTLE-MODE] failed to lift " + method.Name + " owner=" + owner + ": " + ex.Message);
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
                if (patch == null || string.IsNullOrEmpty(patch.owner) || patch.owner == SubModule.HarmonyId)
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

        private static string ReadConfig()
        {
            try
            {
                string binDir = Path.GetDirectoryName(typeof(BattleMode).Assembly.Location);
                string moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                string configPath = Path.Combine(moduleRoot, ConfigFileName);
                if (!File.Exists(configPath))
                {
                    File.WriteAllText(configPath, "{\n  \"battleMode\": \"auto\"\n}\n");
                    Log.Info("[BATTLE-MODE] wrote default " + ConfigFileName + " (battleMode=auto)");
                    return "auto";
                }
                string text = File.ReadAllText(configPath);
                System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, "\"battleMode\"\\s*:\\s*\"(auto|solo|coop)\"");
                if (match.Success)
                {
                    Log.Info("[BATTLE-MODE] config: battleMode=" + match.Groups[1].Value);
                    return match.Groups[1].Value;
                }
                // legacy v2.0 key
                if (System.Text.RegularExpressions.Regex.IsMatch(text, "\"soloVanillaBattles\"\\s*:\\s*false"))
                {
                    Log.Info("[BATTLE-MODE] legacy config soloVanillaBattles=false -> coop");
                    return "coop";
                }
                Log.Info("[BATTLE-MODE] config unreadable, defaulting to auto");
                return "auto";
            }
            catch (Exception ex)
            {
                Log.Info("[BATTLE-MODE] config read failed, defaulting to auto: " + ex.Message);
                return "auto";
            }
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

        private static Type SessionType
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    try
                    {
                        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            string name = assembly.GetName().Name;
                            if (name != "BannerlordTogether")
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
                                if (type != null && type.Name == "CoopSession")
                                {
                                    _sessionType = type;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info("[PEER-DETECT] session type lookup failed: " + ex.Message);
                    }
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
            Type type = SessionType;
            if (type == null)
            {
                return null; // co-op mod absent or unreadable
            }
            if (ReadStaticBool("IsClient") == true)
            {
                return true;
            }
            object server = ReadStaticMember(type, "Server");
            if (server == null)
            {
                // no server object -> no hosting session is up -> nobody can be connected
                return false;
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
