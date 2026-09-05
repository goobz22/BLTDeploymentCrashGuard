using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// ROOT-CAUSE fix for the battle-load crash (2026-09-04), proven from IL + live captures:
    ///
    /// TaleWorlds.MountAndBlade.MovementOrder is a `beforefieldinit` STRUCT. Its static
    /// constructor builds six default orders (Null/Charge/Retreat/Stop/Advance/FallBack) by
    /// calling the instance ctor, whose one null-capable line reads Mission.Current.CurrentTime.
    /// Because the type is `beforefieldinit`, the CLR may run that static ctor at ANY point
    /// before the first static-field access — including early during type preparation triggered
    /// by JIT-compiling / Harmony-patching a method that merely references the type (Formation,
    /// OrderController). When it runs before a mission exists, Mission.Current is null → the
    /// ctor NREs → the type initializer fails → .NET caches the failure PERMANENTLY, and every
    /// battle for the rest of the process dies at Formation.ResetAux with a
    /// TypeInitializationException. Our own Formation/OrderController patches (added in v1.3.0)
    /// are what make the CLR prepare MovementOrder that early, so this mod caused it.
    ///
    /// Fix, in two parts, applied BEFORE any other patch in PayloadEntry.Apply:
    ///  1. A transpiler on MovementOrder..ctor(MovementOrderEnum) rewrites the single
    ///     `Mission.Current.CurrentTime` read to a null-safe helper (returns 0f when there is no
    ///     mission). The six default template orders built at init simply get gameTime 0 — they
    ///     are singletons whose tick timer is irrelevant; real orders built during gameplay have
    ///     a live mission and get the true time.
    ///  2. We then force the static ctor to run NOW, under the patched (safe) ctor, so the type
    ///     is initialized SUCCESSFULLY and cached good for the whole process — nothing can poison
    ///     it afterwards, whatever prepares it or when.
    ///
    /// The load log states the outcome, which also disambiguates the two open hypotheses:
    ///  - "initialized safely (patched N site(s))"  -> fix active, crash prevented.
    ///  - "ALREADY poisoned before guard"           -> the failing init happened before our
    ///     payload even loaded; the fix must move earlier (harness SubModule).
    /// </summary>
    internal static class MovementOrderTypeInitGuard
    {
        private static int _patchedSites;
        private static bool _nullTimeNoted;

        internal static void ApplyEarly(Harmony harmony)
        {
            try
            {
                ConstructorInfo ctor = AccessTools.Constructor(typeof(MovementOrder), new[] { typeof(MovementOrder.MovementOrderEnum) });
                if (ctor == null)
                {
                    Log.Info("[MO-INIT] MovementOrder..ctor(MovementOrderEnum) not found — type-init guard inactive");
                    return;
                }

                harmony.Patch(ctor, transpiler: new HarmonyMethod(typeof(MovementOrderTypeInitGuard), nameof(Transpiler)));

                // Force the static ctor to run now, under the patched null-safe instance ctor,
                // so the type is initialized successfully and cached good for the process.
                try
                {
                    RuntimeCompilerServicesRunClassCtor();
                    Log.Info("[MO-INIT] MovementOrder initialized safely (patched " + _patchedSites +
                             " site(s)) — the beforefieldinit type-init battle crash is prevented for this session");
                }
                catch (TypeInitializationException tie)
                {
                    Log.Info("[MO-INIT] MovementOrder was ALREADY poisoned before this guard could patch it (origin earlier than payload load): " +
                             (tie.InnerException != null ? tie.InnerException.GetType().Name + ": " + tie.InnerException.Message : tie.Message) +
                             " — the fix must move into the harness SubModule (patched " + _patchedSites + " site(s))");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[MO-INIT] apply failed: " + ex.Message);
            }
        }

        private static void RuntimeCompilerServicesRunClassCtor()
        {
            RuntimeHelpers.RunClassConstructor(typeof(MovementOrder).TypeHandle);
        }

        /// <summary>Replace `call Mission::get_Current; callvirt Mission::get_CurrentTime`
        /// (which NREs when Mission.Current is null) with a single call to SafeCurrentTime.
        /// Stack effect is identical: the pair nets one float pushed; so does the helper.</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo getCurrent = AccessTools.PropertyGetter(typeof(Mission), "Current");
            MethodInfo getCurrentTime = AccessTools.PropertyGetter(typeof(Mission), "CurrentTime");
            MethodInfo safe = AccessTools.Method(typeof(MovementOrderTypeInitGuard), nameof(SafeCurrentTime));

            for (int i = 0; i < list.Count; i++)
            {
                if (getCurrentTime != null && list[i].Calls(getCurrentTime) &&
                    i > 0 && getCurrent != null && list[i - 1].Calls(getCurrent))
                {
                    // preserve any labels/blocks on the get_Current instruction
                    list[i - 1].opcode = OpCodes.Call;
                    list[i - 1].operand = safe;
                    list[i].opcode = OpCodes.Nop;
                    list[i].operand = null;
                    _patchedSites++;
                }
            }
            if (_patchedSites == 0)
            {
                Log.Info("[MO-INIT] transpiler found no Mission.Current.CurrentTime site in MovementOrder..ctor (game changed?) — leaving ctor unmodified");
            }
            return list;
        }

        /// <summary>Mission.Current.CurrentTime, null-safe. Returns 0f when there is no active
        /// mission (the only time the original NREs). Logs the first null hit so the fix firing
        /// at init is visible.</summary>
        public static float SafeCurrentTime()
        {
            try
            {
                Mission m = Mission.Current;
                if (m == null)
                {
                    if (!_nullTimeNoted)
                    {
                        _nullTimeNoted = true;
                        Log.Info("[MO-INIT] MovementOrder constructed with no active mission — returned time 0 instead of crashing (this is the fix firing)");
                    }
                    return 0f;
                }
                return m.CurrentTime;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
