using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Lets the player close (and re-open) the castle/town gate while WALKING AROUND a
    /// settlement. Why there is no F prompt in vanilla (proven from the installed build's
    /// IL, 2026-08-30): civilian missions call CastleGate.OpenDoorAndDisableGateForCivilian-
    /// Mission, and SetInitialStateOfGate then force-opens the door and calls
    /// MissionObject.SetDisabled(true) on the whole gate machine — every standing point is
    /// disabled with it, GetActionTextForStandingPoint is never consulted, and CloseDoor()
    /// itself early-outs on IsDisabled. On top of that, AfterMissionStart sets the usable
    /// team to Mission.DefenderTeam, and StandingPointWithTeamLimit.IsDisabledForAgent
    /// requires agent.Team == UsableTeam — in a civilian mission that never matches the
    /// player's team. Three locks, all deliberate vanilla "gates are scenery in town" design.
    ///
    /// Fix (postfix on AfterMissionStart, civilian gates only): flip IsDisabled back off on
    /// the gate and each standing point, and set the usable team to the player's team. The
    /// nav-mesh ability flags vanilla cleared stay as vanilla left them for the OPEN state —
    /// pathing while open is untouched; closing goes through vanilla's own CloseDoor
    /// (animation, SetGateNavMeshState, colliders), so a closed civilian gate behaves
    /// exactly like a closed siege gate. Works in vanilla and co-op alike — settlement
    /// visits are local missions on every peer and BT has no gate code (assembly scan).
    /// A finalizer on the gate ticks catches any siege-only assumption that surfaces now
    /// that civilian gates tick (none known; self-disabling insurance).
    /// </summary>
    internal static class CivilianGateCloseFix
    {
        private static FieldInfo _civilianField;
        private static MethodInfo _setIsDisabled;
        private static int _lastTickErrorLog;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _civilianField = AccessTools.Field(typeof(CastleGate), "_civilianMission");
                _setIsDisabled = AccessTools.PropertySetter(typeof(MissionObject), "IsDisabled");
                MethodInfo afterStart = AccessTools.Method(typeof(CastleGate), "AfterMissionStart");
                if (_civilianField == null || _setIsDisabled == null || afterStart == null)
                {
                    Log.Info("[GATE] civilian gate-close fix inactive — vanilla members not resolved (game update?)");
                    Diag.Report("civilian-gate-fix", false, "members not resolved");
                    return;
                }
                harmony.Patch(afterStart, null, new HarmonyMethod(typeof(CivilianGateCloseFix), nameof(AfterMissionStartPostfix)));
                foreach (string tick in new[] { "OnTick", "ServerTick" })
                {
                    MethodInfo m = AccessTools.Method(typeof(CastleGate), tick);
                    if (m != null)
                    {
                        harmony.Patch(m, null, null, null, new HarmonyMethod(typeof(CivilianGateCloseFix), nameof(TickFinalizer)));
                    }
                }
                Log.Info("[GATE] civilian gate-close fix active — settlement gates get their F Open/Close interaction back");
                Diag.Report("civilian-gate-fix", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[GATE] apply failed: " + ex.Message);
                Diag.Report("civilian-gate-fix", false, ex.Message);
            }
        }

        private static void AfterMissionStartPostfix(CastleGate __instance)
        {
            try
            {
                if (!(_civilianField.GetValue(__instance) is bool civilian) || !civilian)
                {
                    return; // battle/siege gates keep vanilla behavior untouched
                }
                Team playerTeam = Mission.Current != null ? Mission.Current.PlayerTeam : null;
                if (playerTeam == null)
                {
                    return; // nobody local to use the gate — leave it as scenery
                }
                _setIsDisabled.Invoke(__instance, new object[] { false });
                foreach (StandingPoint point in __instance.StandingPoints)
                {
                    _setIsDisabled.Invoke(point, new object[] { false });
                }
                __instance.SetUsableTeam(playerTeam);
                SelfHealing.RecordFire("civilian-gate-fix");
                Log.Info("[GATE] restored gate interaction in this settlement visit (" +
                         __instance.StandingPoints.Count + " standing point(s); F closes/opens the gate)");
            }
            catch (Exception ex)
            {
                Log.Info("[GATE] could not restore civilian gate interaction: " + ex.Message);
            }
        }

        /// <summary>Civilian scenes never ticked gates before this fix; if a tick trips on a
        /// siege-only assumption, skip that tick instead of crashing the visit.</summary>
        private static Exception TickFinalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("civilian-gate-fix");
            int now = Environment.TickCount;
            if (_lastTickErrorLog == 0 || now - _lastTickErrorLog >= 5000 || now < _lastTickErrorLog)
            {
                _lastTickErrorLog = now;
                Log.Info("[GATE] SUPPRESSED gate tick error (siege-only assumption in a civilian scene?): " + __exception.Message);
            }
            return null;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool resolved = _civilianField != null && _setIsDisabled != null &&
                            AccessTools.Method(typeof(CastleGate), "AfterMissionStart") != null &&
                            AccessTools.Method(typeof(CastleGate), "CloseDoor") != null &&
                            AccessTools.Method(typeof(CastleGate), "SetUsableTeam") != null;
            bool inertOnNull = TickFinalizer(null) == null;
            bool pass = resolved && inertOnNull;
            return SelfHealing.TestResult.Of("civilian-gate-fix.contract", pass,
                pass ? "vanilla members re-resolved; tick finalizer inert on null"
                     : "resolved=" + resolved + " inertOnNull=" + inertOnNull);
        }
    }
}
