using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// "Solo vanilla battles" mode (default ON, toggle in guardconfig.json).
    ///
    /// Problem it solves: when hosting BannerlordTogether co-op with zero connected
    /// players, its battle-mission pipeline strips the player's side out of missions —
    /// no player agent, no troops (proven 2026-08-18: empty 0/0 formations on a raid
    /// battle; siege CTD in DeploymentMissionController.SetupTeams).
    ///
    /// The fix uses only Harmony's public patch-management API: enumerate the patches
    /// installed on a fixed list of NATIVE battle/deployment/spawn methods and remove
    /// every foreign owner's patches from those methods, restoring pure vanilla
    /// behavior for battle missions. Campaign-layer co-op patches (time control,
    /// hosting, finances, map sync) are deliberately left untouched. No third-party
    /// code is read, copied, or modified — patches are simply not allowed to hook
    /// these methods in this process, which is runtime configuration, equivalent to
    /// partially disabling a mod.
    ///
    /// When hosting with friends connected, set {"soloVanillaBattles": false} in
    /// Modules/BLTDeploymentCrashGuard/guardconfig.json and restart, so the co-op
    /// mod's synced-battle pipeline stays in charge.
    /// </summary>
    internal static class SoloVanillaBattles
    {
        private const string ConfigFileName = "guardconfig.json";

        // Native methods whose foreign patches are removed in solo mode. Battle-mission
        // scope only — campaign/map co-op machinery is intentionally not listed.
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

        private static bool? _enabled;
        private static bool _announced;

        internal static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    _enabled = ReadConfig();
                }
                return _enabled.Value;
            }
        }

        /// <summary>
        /// Idempotent: safe to call from multiple lifecycle points (module screen,
        /// game start, mission init) — later calls only act if a foreign patch
        /// reappeared on a listed method.
        /// </summary>
        internal static void Sweep(Harmony harmony, string reason)
        {
            if (harmony == null || !Enabled)
            {
                return;
            }
            try
            {
                int removedTotal = 0;
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
                            if (method.Name != methodName || method.IsAbstract)
                            {
                                continue;
                            }
                            removedTotal += RemoveForeignPatches(harmony, method);
                        }
                    }
                }
                if (removedTotal > 0)
                {
                    Log.Info("[SOLO-VANILLA] removed " + removedTotal + " foreign patch(es) from battle-mission methods (" + reason + ") — battles run native");
                    if (!_announced)
                    {
                        _announced = true;
                        Log.Screen("solo vanilla battles ON — battles run native this session (guardconfig.json to disable before hosting friends)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[SOLO-VANILLA] sweep failed (" + reason + "): " + ex);
            }
        }

        private static int RemoveForeignPatches(Harmony harmony, MethodInfo method)
        {
            Patches patchInfo = Harmony.GetPatchInfo(method);
            if (patchInfo == null)
            {
                return 0;
            }
            HashSet<string> foreignOwners = new HashSet<string>(StringComparer.Ordinal);
            CollectForeignOwners(patchInfo.Prefixes, foreignOwners);
            CollectForeignOwners(patchInfo.Postfixes, foreignOwners);
            CollectForeignOwners(patchInfo.Finalizers, foreignOwners);
            CollectForeignOwners(patchInfo.Transpilers, foreignOwners);
            int removed = 0;
            foreach (string owner in foreignOwners)
            {
                try
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, owner);
                    removed++;
                    Log.Info("[SOLO-VANILLA] unpatched " + (method.DeclaringType != null ? method.DeclaringType.Name : "?") + "." + method.Name + " (owner=" + owner + ")");
                }
                catch (Exception ex)
                {
                    Log.Info("[SOLO-VANILLA] failed to unpatch " + method.Name + " owner=" + owner + ": " + ex.Message);
                }
            }
            return removed;
        }

        private static void CollectForeignOwners(IEnumerable<Patch> patches, HashSet<string> owners)
        {
            if (patches == null)
            {
                return;
            }
            foreach (Patch patch in patches)
            {
                if (patch != null && !string.IsNullOrEmpty(patch.owner) && patch.owner != SubModule.HarmonyId)
                {
                    owners.Add(patch.owner);
                }
            }
        }

        private static bool ReadConfig()
        {
            try
            {
                string binDir = Path.GetDirectoryName(typeof(SoloVanillaBattles).Assembly.Location);
                string moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                string configPath = Path.Combine(moduleRoot, ConfigFileName);
                if (!File.Exists(configPath))
                {
                    File.WriteAllText(configPath, "{\n  \"soloVanillaBattles\": true\n}\n");
                    Log.Info("[SOLO-VANILLA] wrote default " + ConfigFileName + " (enabled). Set to false + restart before hosting friends.");
                    return true;
                }
                string text = File.ReadAllText(configPath);
                bool disabled = System.Text.RegularExpressions.Regex.IsMatch(text, "\"soloVanillaBattles\"\\s*:\\s*false");
                Log.Info("[SOLO-VANILLA] config: soloVanillaBattles=" + (!disabled).ToString().ToLowerInvariant());
                return !disabled;
            }
            catch (Exception ex)
            {
                Log.Info("[SOLO-VANILLA] config read failed, defaulting to enabled: " + ex.Message);
                return true;
            }
        }
    }
}
