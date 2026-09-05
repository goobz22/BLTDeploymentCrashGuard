using System;
using System.Collections.Generic;
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
    ///
    /// Health component `map-click-speed`; fire id the same; self-test
    /// `map-click-speed.contract` pins MapScreen, the click handler, the time-control setter and
    /// the veto decision table (added 2026-09-04 — this keeper used to be invisible to MOD HEALTH).
    /// </summary>
    internal static class MapClickSpeedKeeper
    {
        private const string Component = "map-click-speed";
        private const string Tag = "[CLICK-SPEED]";
        private const string MapScreenType = "SandBox.View.Map.MapScreen";
        private const string ClickMethod = "HandleLeftMouseButtonClick";
        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        [ThreadStatic]
        private static bool _inMapClick;
        private static bool _logged;
        private static bool _applied;
        private static bool _testRegistered;

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                Type mapScreen = AccessTools.TypeByName(MapScreenType);
                if (mapScreen == null)
                {
                    Log.Info(Tag + " " + MapScreenType + " not found — keeper idle (game update?)");
                    Diag.Report(Component, false, MapScreenType + " not found (game update?)");
                    return;
                }
                int count = 0;
                foreach (MethodInfo method in ClickMethods(mapScreen))
                {
                    harmony.Patch(method,
                        new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(ClickPrefix)),
                        null, null,
                        new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(ClickFinalizer)));
                    count++;
                }
                if (count == 0)
                {
                    Log.Info(Tag + " MapScreen." + ClickMethod + " not found — keeper idle (game update?)");
                    Diag.Report(Component, false, "MapScreen." + ClickMethod + " not found (game update?)");
                    return;
                }
                MethodInfo setter = Setter();
                if (setter == null)
                {
                    Log.Info(Tag + " Campaign.set_TimeControlMode not found — keeper idle (game update?)");
                    Diag.Report(Component, false, "Campaign.set_TimeControlMode not found (game update?)");
                    return;
                }
                harmony.Patch(setter, new HarmonyMethod(typeof(MapClickSpeedKeeper), nameof(SetModePrefix)));
                _applied = true;
                Diag.Report(Component, true, "");
                Log.Info(Tag + " map-click fast-forward keeper active (" + count + " click method(s))");
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        private static List<MethodInfo> ClickMethods(Type mapScreen)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            foreach (MethodInfo method in mapScreen.GetMethods(AllDeclared))
            {
                if (method.Name == ClickMethod && !method.IsAbstract)
                {
                    found.Add(method);
                }
            }
            return found;
        }

        private static MethodInfo Setter()
        {
            return AccessTools.Method(typeof(Campaign), "set_TimeControlMode");
        }

        /// <summary>The whole decision, engine-free so the self-test can pin it: veto exactly the
        /// UnstoppableFastForward -> StoppablePlay downgrade requested from inside a map click.
        /// Everything else (unpausing on click, vanilla's own keep-FF case, an explicit pause) is
        /// left to the game.</summary>
        internal static bool ShouldKeepFastForward(bool inMapClick, CampaignTimeControlMode requested, CampaignTimeControlMode current)
        {
            return inMapClick &&
                   requested == CampaignTimeControlMode.StoppablePlay &&
                   current == CampaignTimeControlMode.UnstoppableFastForward;
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
                if (__instance != null && ShouldKeepFastForward(_inMapClick, value, __instance.TimeControlMode))
                {
                    SelfHealing.RecordFire(Component); // one fire per kept click — feeds GUARD ACTIVITY
                    if (!_logged)
                    {
                        _logged = true;
                        Log.Info(Tag + " kept UnstoppableFastForward through a map click (vanilla keep-FF option does not recognize the unstoppable variant co-op uses)");
                    }
                    TimeVeto.Note("CLICK-SPEED"); // lets the [TIME] tracer name which prefix vetoed the write
                    return false;
                }
            }
            catch
            {
            }
            return true;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type mapScreen = AccessTools.TypeByName(MapScreenType);
            bool click = mapScreen != null && ClickMethods(mapScreen).Count > 0;
            bool setter = Setter() != null;
            bool decisions =
                ShouldKeepFastForward(true, CampaignTimeControlMode.StoppablePlay, CampaignTimeControlMode.UnstoppableFastForward) &&
                !ShouldKeepFastForward(false, CampaignTimeControlMode.StoppablePlay, CampaignTimeControlMode.UnstoppableFastForward) &&
                !ShouldKeepFastForward(true, CampaignTimeControlMode.StoppablePlay, CampaignTimeControlMode.Stop) &&                    // clicking while paused still unpauses
                !ShouldKeepFastForward(true, CampaignTimeControlMode.Stop, CampaignTimeControlMode.UnstoppableFastForward) &&          // an explicit pause always wins
                !ShouldKeepFastForward(true, CampaignTimeControlMode.StoppableFastForward, CampaignTimeControlMode.UnstoppableFastForward) &&
                !ShouldKeepFastForward(true, CampaignTimeControlMode.StoppablePlay, CampaignTimeControlMode.StoppableFastForward);       // vanilla's own keep-FF option covers this one
            bool pass = mapScreen != null && click && setter && decisions;
            return SelfHealing.TestResult.Of(Component + ".contract", pass,
                pass ? "MapScreen." + ClickMethod + ", Campaign.set_TimeControlMode and the veto table verified"
                     : "mapScreen=" + (mapScreen != null) + " click=" + click + " setter=" + setter + " decisions=" + decisions);
        }
    }
}
