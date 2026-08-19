using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// timeAlwaysFlows (guardconfig.json, default true): never auto-hold campaign time
    /// when the main party goes idle.
    ///
    /// Vanilla mechanism (verified in Campaign.TickMapTime): every tick sets
    /// IsMainPartyWaiting = MobileParty.MainParty.ComputeIsWaiting(), and the
    /// Stoppable play/fast-forward modes advance time only while that is false — so
    /// arriving at a clicked destination silently halts time without changing mode.
    /// This postfix forces ComputeIsWaiting to false for the MAIN party only, so time
    /// keeps flowing at the chosen speed. Real pauses (Stop mode via the pause button,
    /// menus, encounters) are untouched, as are AI parties and the wait-menu mode
    /// (UnstoppableFastForwardForPartyWaitTime), which never consults this flag.
    /// </summary>
    internal static class TimeFlowPatch
    {
        private static bool? _enabled;
        private static bool _loggedActive;

        private static bool Enabled
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

        internal static void Apply(Harmony harmony)
        {
            try
            {
                int count = 0;
                foreach (MethodInfo method in typeof(MobileParty).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "ComputeIsWaiting" || method.IsAbstract)
                    {
                        continue;
                    }
                    harmony.Patch(method, null, new HarmonyMethod(typeof(TimeFlowPatch), nameof(Postfix)));
                    count++;
                }
                Log.Info("[TIME-FLOW] timeAlwaysFlows=" + Enabled.ToString().ToLowerInvariant() + " (patched " + count + " method(s))");
            }
            catch (Exception ex)
            {
                Log.Info("[TIME-FLOW] apply failed: " + ex.Message);
            }
        }

        private static void Postfix(MobileParty __instance, ref bool __result)
        {
            try
            {
                if (!__result || !Enabled || __instance == null || !__instance.IsMainParty)
                {
                    return;
                }
                __result = false;
                if (!_loggedActive)
                {
                    _loggedActive = true;
                    Log.Info("[TIME-FLOW] suppressing main-party idle-hold — time keeps flowing at the chosen speed (guardconfig timeAlwaysFlows=false to revert)");
                }
            }
            catch
            {
            }
        }

        private static bool ReadConfig()
        {
            try
            {
                string binDir = Path.GetDirectoryName(typeof(TimeFlowPatch).Assembly.Location);
                string configPath = Path.Combine(Path.GetFullPath(Path.Combine(binDir, "..", "..")), "guardconfig.json");
                if (File.Exists(configPath))
                {
                    string text = File.ReadAllText(configPath);
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, "\"timeAlwaysFlows\"\\s*:\\s*false"))
                    {
                        return false;
                    }
                }
            }
            catch
            {
            }
            return true;
        }
    }
}
