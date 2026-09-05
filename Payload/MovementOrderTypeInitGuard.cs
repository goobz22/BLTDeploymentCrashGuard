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
    /// Health (component "movementorder-typeinit", critical) and a self-test that pins the
    /// premise (struct + beforefieldinit), the ctor, the one transpiled site and the helper. The
    /// load log states the outcome:
    ///  - "initialized safely (patched N site(s))"  -> fix active, crash prevented.
    ///  - "ALREADY poisoned before guard"           -> the failing init happened before our
    ///     payload even loaded; the fix must move earlier (harness SubModule).
    /// Load-time fix: takes effect on a fresh launch, not on a hot-reload.
    /// </summary>
    internal static class MovementOrderTypeInitGuard
    {
        internal const string Component = "movementorder-typeinit";
        private const string Tag = "[MO-INIT]";
        private static int _patchedSites;
        private static bool _nullTimeNoted;
        private static bool _applied;

        internal static void ApplyEarly(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            _applied = true;
            try
            {
                SelfHealing.RegisterTest(SelfTest);
                ConstructorInfo ctor = AccessTools.Constructor(typeof(MovementOrder), new[] { typeof(MovementOrder.MovementOrderEnum) });
                if (ctor == null)
                {
                    Diag.Report(Component, false, "MovementOrder..ctor(MovementOrderEnum) not found", critical: true);
                    Log.Info(Tag + " MovementOrder..ctor(MovementOrderEnum) not found — type-init guard inactive (game update?)");
                    return;
                }

                harmony.Patch(ctor, transpiler: new HarmonyMethod(typeof(MovementOrderTypeInitGuard), nameof(Transpiler)));

                // Force the static ctor to run now, under the patched null-safe instance ctor,
                // so the type is initialized successfully and cached good for the process.
                try
                {
                    RuntimeHelpers.RunClassConstructor(typeof(MovementOrder).TypeHandle);
                    bool ok = _patchedSites == 1;
                    Diag.Report(Component, ok, ok ? "" : "transpiled " + _patchedSites + " site(s), expected 1", critical: !ok);
                    Log.Info(Tag + " MovementOrder initialized safely (patched " + _patchedSites +
                             " site(s)) — the beforefieldinit type-init battle crash is prevented for this session");
                }
                catch (TypeInitializationException tie)
                {
                    string inner = tie.InnerException != null ? tie.InnerException.GetType().Name + ": " + tie.InnerException.Message : tie.Message;
                    Diag.Report(Component, false, "already poisoned before payload load: " + inner, critical: true);
                    Log.Info(Tag + " MovementOrder was ALREADY poisoned before this guard could patch it (origin earlier than payload load): " +
                             inner + " — the fix must move into the harness SubModule (patched " + _patchedSites + " site(s))");
                }
            }
            catch (Exception ex)
            {
                Diag.Report(Component, false, ex.Message, critical: true);
                Log.Info(Tag + " apply failed: " + ex.Message);
            }
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

            // Count per INVOCATION: Harmony re-runs the whole transpiler chain from the original IL
            // whenever the same method is patched again (the dev origin probe patches this ctor too,
            // and a hot-reload applies the new generation before unpatching the old one), so a
            // cumulative static drifted to 2 or 0 and falsely failed health + self-test while the
            // fix was working (review 2026-09-04). A site already rewritten by a previous
            // generation's transpiler counts as handled.
            int sites = 0;
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
                    sites++;
                }
                else if (safe != null && list[i].opcode == OpCodes.Call && ReferenceEquals(list[i].operand, safe))
                {
                    sites++; // already rewritten by an earlier generation's transpiler
                }
            }
            _patchedSites = sites;
            if (sites == 0)
            {
                Log.Info(Tag + " transpiler found no Mission.Current.CurrentTime site in MovementOrder..ctor (game changed?) — leaving ctor unmodified");
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
                        Log.Info(Tag + " MovementOrder constructed with no active mission — returned time 0 instead of crashing (this is the fix firing)");
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

        private static SelfHealing.TestResult SelfTest()
        {
            Type type = typeof(MovementOrder);
            bool ctor = AccessTools.Constructor(type, new[] { typeof(MovementOrder.MovementOrderEnum) }) != null;
            // The premise of the fix: a beforefieldinit value type. If a game update changes
            // either, the hazard may be gone and this guard should be re-evaluated.
            bool premise = type.IsValueType && (type.Attributes & TypeAttributes.BeforeFieldInit) != 0;
            bool site = _patchedSites == 1;
            bool helper;
            try { SafeCurrentTime(); helper = true; } catch { helper = false; }
            bool pass = ctor && premise && site && helper;
            return SelfHealing.TestResult.Of("movementorder-typeinit.contract", pass,
                pass ? "ctor re-resolved; struct+beforefieldinit premise holds; 1 site transpiled; null-safe helper callable"
                     : "ctor=" + ctor + " premise=" + premise + " sites=" + _patchedSites + " helper=" + helper);
        }
    }
}
