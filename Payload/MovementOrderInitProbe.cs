using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// ORIGIN probe for the battle-load crash (2026-09-04). Proven so far, from IL + live
    /// captures: the fatal exception is TypeInitializationException on MovementOrder, whose
    /// static constructor builds six default orders via MovementOrder..ctor(enum); that ctor's
    /// only null-capable line is Mission.Current.CurrentTime. At the point the crash is LOGGED
    /// (Formation.ResetAux inside Mission.AfterStart) Mission.Current is already live, and
    /// Mission.Initialize sets Mission._current before AfterStart — so that logged throw is a
    /// CACHED RE-THROW of an EARLIER first-touch failure. .NET runs a type initializer once; if
    /// it throws, the failure is cached and every later access re-throws the ORIGINAL exception
    /// (with its original inner + stack) WITHOUT re-running the ctor. So the collateral has been
    /// captured, never the ORIGIN.
    ///
    /// This probe patches the instance constructor MovementOrder..ctor(MovementOrderEnum). Its
    /// first-ever call happens INSIDE the static ctor, so it fires exactly at the origin. It
    /// logs, for the first few constructions, whether Mission.Current is null and the full live
    /// stack + memory — naming the true trigger and proving the null window. A finalizer logs
    /// the NRE at the instant it is really thrown. Off unless tracing=true; no behaviour change.
    /// </summary>
    internal static class MovementOrderInitProbe
    {
        private static int _seen;
        private const int LogFirst = 12; // the six defaults + a few real orders is enough to see the pattern

        internal static void Apply(Harmony harmony)
        {
            try
            {
                var ctor = AccessTools.Constructor(typeof(MovementOrder), new[] { typeof(MovementOrder.MovementOrderEnum) });
                if (ctor == null)
                {
                    Log.Info("[MO-PROBE] MovementOrder..ctor(MovementOrderEnum) not found — probe inactive");
                    return;
                }
                harmony.Patch(ctor,
                    new HarmonyMethod(typeof(MovementOrderInitProbe), nameof(CtorPrefix)),
                    null, null,
                    new HarmonyMethod(typeof(MovementOrderInitProbe), nameof(CtorFinalizer)));
                Log.Info("[MO-PROBE] MovementOrder ctor origin probe active (logs first " + LogFirst + " constructions + any throw)");
            }
            catch (Exception ex)
            {
                Log.Info("[MO-PROBE] apply failed: " + ex.Message);
            }
        }

        private static void CtorPrefix(MovementOrder.MovementOrderEnum orderEnum)
        {
            try
            {
                if (_seen >= LogFirst)
                {
                    return;
                }
                _seen++;
                bool missionNull;
                try { missionNull = Mission.Current == null; } catch { missionNull = true; }
                Log.Info("[MO-PROBE] MovementOrder..ctor #" + _seen + " enum=" + orderEnum +
                         " Mission.Current==null? " + missionNull +
                         "\n   " + RuntimeDiagnostics.MemoryLine() +
                         RuntimeDiagnostics.LiveGameStack(2));
            }
            catch
            {
            }
        }

        private static Exception CtorFinalizer(MovementOrder.MovementOrderEnum orderEnum, Exception __exception)
        {
            if (__exception != null)
            {
                try
                {
                    bool missionNull;
                    try { missionNull = Mission.Current == null; } catch { missionNull = true; }
                    Log.Info("[MO-PROBE] *** ORIGIN THROW in MovementOrder..ctor enum=" + orderEnum +
                             " Mission.Current==null? " + missionNull +
                             " ex=" + __exception.GetType().Name + ": " + __exception.Message +
                             "\n   " + RuntimeDiagnostics.StateContext() +
                             "\n   " + RuntimeDiagnostics.MemoryLine() +
                             RuntimeDiagnostics.LiveGameStack(2));
                }
                catch
                {
                }
            }
            return __exception; // never swallow — the crash still needs to surface as-is
        }
    }
}
