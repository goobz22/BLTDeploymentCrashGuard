using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Fixes "[BT] Marriage is blocked until clan mode is synchronized" when playing ALONE.
    ///
    /// Decompile-proven root cause: BT's ClanModeSyncBehavior.CurrentMode returns Unknown
    /// (enum af.bI = 0) whenever no REMOTE identity snapshot has arrived — and hosting with no
    /// peer connected there never will be one, so clan mode stays Unknown forever and every
    /// clan-mode-gated action (marriage foremost) is blocked for the whole solo session.
    ///
    /// Fix at the state machine, not a vanilla fallback: a TRANSPILER injects a preamble into
    /// the CurrentMode getter — when we are CONFIDENTLY alone (host role, no peer, using the
    /// proven tri-state PeerDetection.AnyRemotePeerConnected(); packet liveness wins) it returns
    /// Separate (af.bi = 1), the correct clan mode for a single player. The moment a peer
    /// connects (or anything is uncertain) the original BT computation runs untouched, so real
    /// co-op sync behaves exactly as BT designed it.
    ///
    /// Why a transpiler: Harmony postfixes cannot rewrite a value-typed result of a foreign
    /// internal enum (three quieter approaches failed SILENTLY in the test rig —
    /// scratchpad/HarmonyEnumTest). The transpiler shape is rig-verified including the JIT
    /// inlining caveat: callers jitted BEFORE the patch keep the inlined original, which is why
    /// this applies at module load, before any campaign code compiles.
    /// </summary>
    internal static class ClanModeSoloFix
    {
        private static bool _applied;
        private static MethodBase _getter;

        internal static void Apply(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                Type behavior = AccessTools.TypeByName("BannerlordTogether.ClanModeSyncBehavior");
                _getter = behavior != null ? AccessTools.PropertyGetter(behavior, "CurrentMode") : null;
                if (_getter == null)
                {
                    // BT absent or renamed — retried from the later lifecycle hooks via PayloadEntry.
                    Diag.Report("clanmode-solo-fix", false, "ClanModeSyncBehavior.CurrentMode not found");
                    return;
                }
                harmony.Patch(_getter, null, null, new HarmonyMethod(typeof(ClanModeSoloFix), nameof(Transpiler)));
                _applied = true;
                Log.Info("[CLANMODE-FIX] CurrentMode getter patched — solo host reports Separate instead of waiting-for-peer Unknown (marriage unblocked); inert when a peer is connected");
                Diag.Report("clanmode-solo-fix", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CLANMODE-FIX] apply failed: " + ex.Message);
                Diag.Report("clanmode-solo-fix", false, ex.Message);
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            // if (ClanModeSoloDecider.ShouldForceSeparate()) return (af)1; — then the original body.
            Label continueOriginal = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClanModeSoloDecider), nameof(ClanModeSoloDecider.ShouldForceSeparate)));
            yield return new CodeInstruction(OpCodes.Brfalse, continueOriginal);
            yield return new CodeInstruction(OpCodes.Ldc_I4_1);
            yield return new CodeInstruction(OpCodes.Ret);
            bool first = true;
            foreach (CodeInstruction instruction in instructions)
            {
                if (first)
                {
                    instruction.labels.Add(continueOriginal);
                    first = false;
                }
                yield return instruction;
            }
        }

        /// <summary>Reads the LIVE post-patch value the way BT's callers will (reflection invoke
        /// goes through the detoured entry, never an inlined copy).</summary>
        internal static byte? ReadLiveMode()
        {
            try
            {
                Type behavior = AccessTools.TypeByName("BannerlordTogether.ClanModeSyncBehavior");
                object instance = behavior != null ? AccessTools.Property(behavior, "Instance")?.GetValue(null) : null;
                MethodInfo getter = behavior != null ? AccessTools.PropertyGetter(behavior, "CurrentMode") : null;
                if (instance == null || getter == null)
                {
                    return null;
                }
                return Convert.ToByte(getter.Invoke(instance, null));
            }
            catch
            {
                return null;
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type behavior = AccessTools.TypeByName("BannerlordTogether.ClanModeSyncBehavior");
            bool getterExists = behavior != null && AccessTools.PropertyGetter(behavior, "CurrentMode") != null;
            // Decision contract: forcing requires a CONFIDENT solo verdict — a null/unknown peer
            // state must never force (fail toward BT's own behavior).
            bool conservativeOnUnknown = !ClanModeSoloDecider.Decide(null, isHost: true);
            bool conservativeAsClient = !ClanModeSoloDecider.Decide(false, isHost: false);
            bool forcesWhenAlone = ClanModeSoloDecider.Decide(false, isHost: true);
            bool pass = getterExists && _applied && conservativeOnUnknown && conservativeAsClient && forcesWhenAlone;
            return SelfHealing.TestResult.Of("clanmode-solo-fix.contract", pass,
                pass ? "getter re-resolved; patched; decider forces only on confident host-alone"
                     : "getterExists=" + getterExists + " applied=" + _applied + " unknownSafe=" + conservativeOnUnknown +
                       " clientSafe=" + conservativeAsClient + " forcesAlone=" + forcesWhenAlone);
        }
    }

    /// <summary>Public so the transpiled call inside BT's getter can always bind to it.</summary>
    public static class ClanModeSoloDecider
    {
        private static int _lastCheckTick;
        private static bool _lastVerdict;
        private static bool _forcingLogged;

        public static bool ShouldForceSeparate()
        {
            try
            {
                // The getter can be called every frame — recompute the reflection-heavy state at
                // most every 2s and serve the cached verdict between.
                int now = Environment.TickCount;
                if (_lastCheckTick != 0 && now - _lastCheckTick < 2000 && now >= _lastCheckTick)
                {
                    return _lastVerdict;
                }
                _lastCheckTick = now;

                bool isHost = PeerDetection.ReadCoopStaticBool("IsHost") == true;
                bool? peer = PeerDetection.AnyRemotePeerConnected();
                bool verdict = Decide(peer, isHost);
                if (verdict != _lastVerdict || (verdict && !_forcingLogged))
                {
                    _forcingLogged = verdict;
                    Log.Info(verdict
                        ? "[CLANMODE-FIX] solo host confirmed (no peer) — clan mode now reads Separate; marriage and other clan-gated actions unblocked"
                        : "[CLANMODE-FIX] peer present/uncertain — BT's own clan-mode sync in charge");
                }
                _lastVerdict = verdict;
                return verdict;
            }
            catch
            {
                return false; // any failure: leave BT's behavior untouched
            }
        }

        internal static bool Decide(bool? anyRemotePeer, bool isHost)
        {
            // Force ONLY on a confident "hosting and provably alone". true/null peer → hands off.
            return isHost && anyRemotePeer == false;
        }
    }
}
