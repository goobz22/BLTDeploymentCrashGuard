using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Keeps fast-forward through map clicks in co-op.
    ///
    /// Vanilla's "map double click behavior = keep speed" option only preserves
    /// StoppableFastForward on click (MapScreen.HandleClickTimeChange checks mode==4).
    /// The co-op mod enforces the UNSTOPPABLE fast-forward variant, which that check
    /// does not recognize — so every click-to-move drops the session to normal speed
    /// and the sync then yanks it back up, producing the FF flip-flop seen 2026-08-19
    /// 20:18-20:19 (every UnstoppableFastForward -> StoppablePlay came from
    /// MapScreen.HandleLeftMouseButtonClick).
    ///
    /// Fix: while inside HandleLeftMouseButtonClick, veto exactly the
    /// UnstoppableFastForward -> StoppablePlay downgrade. Clicking while paused still
    /// unpauses (Stop -> StoppablePlay untouched), and everything else is vanilla.
    /// </summary>
    internal static class MapClickSpeedKeeper
    {
        [ThreadStatic]
        private static bool _inMapClick;
        private static bool _logged;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type mapScreen = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
                if (mapScreen == null)
                {
                    Log.Info("[CLICK-SPEED] MapScreen not found — keeper idle");
                    return;
                }
                int count = 0;
                foreach (MethodInfo method in mapScreen.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "HandleLeftMouseButtonClick" || method.IsAbstract)
                    {
                        continue;
                    }
                    harmony.Patch(method,
                        new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(ClickPrefix)),
                        null, null,
                        new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(ClickFinalizer)));
                    count++;
                }
                if (count > 0)
                {
                    MethodInfo setter = AccessTools.Method(typeof(Campaign), "set_TimeControlMode");
                    if (setter != null)
                    {
                        harmony.Patch(setter, new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(SetModePrefix)));
                    }
                }
                Log.Info("[CLICK-SPEED] map-click fast-forward keeper active (" + count + " click method(s))");
            }
            catch (Exception ex)
            {
                Log.Info("[CLICK-SPEED] apply failed: " + ex.Message);
            }
        }

        private static void ClickPrefix()
        {
            _inMapClick = true;
        }

        private static Exception ClickFinalizer(Exception __exception)
        {
            _inMapClick = false;
            return __exception;
        }

        private static bool SetModePrefix(Campaign __instance, CampaignTimeControlMode value)
        {
            try
            {
                if (_inMapClick &&
                    value == CampaignTimeControlMode.StoppablePlay &&
                    __instance != null &&
                    __instance.TimeControlMode == CampaignTimeControlMode.UnstoppableFastForward)
                {
                    if (!_logged)
                    {
                        _logged = true;
                        Log.Info("[CLICK-SPEED] kept UnstoppableFastForward through a map click (vanilla keep-FF option does not recognize the unstoppable variant co-op uses)");
                    }
                    return false;
                }
            }
            catch
            {
            }
            return true;
        }
    }
}
