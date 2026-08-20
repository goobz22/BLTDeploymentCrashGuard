using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// FIXES the co-op client permanent half-load at its source.
    ///
    /// Root cause (from BannerlordTogether's own decompiled bootstrap,
    /// CoopSubModule.TryVerifyNativeActionCacheWhenCampaignMapReady): before applying
    /// its deferred Harmony patches it audits the engine's ActionIndexCache — but it
    /// compares the engine's STATIC ActionIndexCache mirror fields (which sit at index
    /// -1, unprimed, in a client session) against fresh native lookups. The mismatch
    /// makes it log "BootstrapAborted reason=action-cache-mismatch ... restartRequired"
    /// and set _harmonyPatchBootstrapAttempted=true, which permanently blocks retry —
    /// so the WHOLE session runs with sync patches unapplied (invisible armies, joins
    /// never registering, speed desync). Its own log proves the NATIVE catalog is fully
    /// loaded (actions=5167, every action code valid, diskLoad=False); only the static
    /// mirror is stale. This is a false negative.
    ///
    /// Fix: a prefix on that verify method. Using THEIR OWN readiness criteria
    /// (num action codes > 0, the four probe actions resolve, no disk load in flight)
    /// we confirm the native catalog is genuinely ready; if so we prime the stale
    /// ActionIndexCache mirror statics from the live catalog and let verification
    /// succeed, so the deferred patches apply. If the catalog is not ready yet we do
    /// nothing and their normal wait logic runs unchanged — the safety intent (never
    /// patch before the catalog loads) is preserved; only the over-strict mirror
    /// requirement is removed.
    ///
    /// All engine access is by-name reflection so it is independent of which assembly
    /// defines ActionIndexCache / MBAnimation.
    /// </summary>
    internal static class ClientBootstrapFix
    {
        private static bool _applied;
        private static bool _primeLogged;
        private static bool _standDownLogged;
        private static bool _resolved;
        private static bool _resolveOk;

        private static FieldInfo _verifiedField;
        private static Type _actionCacheType;
        private static MethodInfo _createMethod;
        private static PropertyInfo _indexProp;
        private static Type _mbAnimationType;
        private static MethodInfo _getNumActionCodes;
        private static MethodInfo _getNumAnimations;
        private static MethodInfo _getActionCodeWithName;
        private static MethodInfo _isAnyAnimationLoadingFromDisk;

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                Type coop = PeerDetection.FindCoopType("CoopSubModule");
                if (coop == null)
                {
                    // BannerlordTogether not loaded — nothing to fix, not a failure.
                    Diag.Report("client-bootstrap-fix", true, "no BT present");
                    return; // retried later in case the co-op assembly loaded late
                }
                if (!ResolveEngineTypes())
                {
                    Log.Info("[CLIENT-FIX] could not resolve engine action-cache types — fix INACTIVE");
                    Diag.Report("client-bootstrap-fix", false, "engine action-cache types not resolved", critical: true);
                    return;
                }
                MethodInfo verify = AccessTools.Method(coop, "TryVerifyNativeActionCacheWhenCampaignMapReady");
                if (verify == null)
                {
                    Log.Info("[CLIENT-FIX] verify method not found — co-op bootstrap fix INACTIVE (mod version changed?)");
                    Diag.Report("client-bootstrap-fix", false, "BT verify method not found (BT updated?)", critical: true);
                    return;
                }
                _verifiedField = AccessTools.Field(coop, "_nativeActionCacheVerified");
                harmony.Patch(verify, new HarmonyMethod(typeof(ClientBootstrapFix), nameof(VerifyPrefix)));
                _applied = true;
                Log.Info("[CLIENT-FIX] co-op action-cache bootstrap fix active (prevents client half-load / BootstrapAborted)");
                Diag.Report("client-bootstrap-fix", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CLIENT-FIX] apply failed: " + ex.Message);
            }
        }

        private static bool ResolveEngineTypes()
        {
            if (_resolved)
            {
                return _resolveOk;
            }
            _resolved = true;
            _resolveOk = ResolveEngineTypesOnce();
            return _resolveOk;
        }

        private static bool ResolveEngineTypesOnce()
        {
            try
            {
                foreach (string candidate in new[] { "TaleWorlds.Core.ActionIndexCache", "TaleWorlds.Engine.ActionIndexCache", "TaleWorlds.MountAndBlade.ActionIndexCache" })
                {
                    _actionCacheType = AccessTools.TypeByName(candidate);
                    if (_actionCacheType != null)
                    {
                        break;
                    }
                }
                foreach (string candidate in new[] { "TaleWorlds.Engine.MBAnimation", "TaleWorlds.Core.MBAnimation", "TaleWorlds.MountAndBlade.MBAnimation" })
                {
                    _mbAnimationType = AccessTools.TypeByName(candidate);
                    if (_mbAnimationType != null)
                    {
                        break;
                    }
                }
                if (_actionCacheType == null || _mbAnimationType == null)
                {
                    return false;
                }
                _createMethod = AccessTools.Method(_actionCacheType, "Create", new[] { typeof(string) });
                _indexProp = AccessTools.Property(_actionCacheType, "Index");
                _getNumActionCodes = AccessTools.Method(_mbAnimationType, "GetNumActionCodes");
                _getNumAnimations = AccessTools.Method(_mbAnimationType, "GetNumAnimations");
                _getActionCodeWithName = AccessTools.Method(_mbAnimationType, "GetActionCodeWithName", new[] { typeof(string) });
                _isAnyAnimationLoadingFromDisk = AccessTools.Method(_mbAnimationType, "IsAnyAnimationLoadingFromDisk");
                // Require EVERY member the fix depends on. Missing any means engine
                // drift — refuse to activate rather than force-pass with dead reflection.
                return _createMethod != null && _indexProp != null && _getActionCodeWithName != null
                    && _getNumActionCodes != null && _getNumAnimations != null && _isAnyAnimationLoadingFromDisk != null;
            }
            catch (Exception ex)
            {
                Log.Info("[CLIENT-FIX] type resolve error: " + ex.Message);
                return false;
            }
        }

        private static bool VerifyPrefix(ref bool __result)
        {
            try
            {
                if (!NativeCatalogReady())
                {
                    return true; // not ready — run their original wait logic unchanged
                }
                // SELF-DISABLE: if the action-cache mirrors are already primed, the audit
                // will pass on its own — BT (or a future TaleWorlds build) fixed the
                // false-negative, so stand down and let their original verify run.
                if (MirrorsAlreadyPrimed())
                {
                    if (!_standDownLogged)
                    {
                        _standDownLogged = true;
                        Log.Info("[CLIENT-FIX] action-cache mirrors already primed — bug not present, standing down (BT/engine handles it)");
                    }
                    return true;
                }
                int primed = PrimeActionIndexCacheMirrors();
                SelfHealing.RecordFire("client-bootstrap-fix");
                if (_verifiedField != null)
                {
                    _verifiedField.SetValue(null, true);
                }
                __result = true;
                if (!_primeLogged)
                {
                    _primeLogged = true;
                    Log.Info("[CLIENT-FIX] native catalog confirmed ready; primed " + primed + " ActionIndexCache mirror field(s) and verified — co-op deferred patches will apply (client half-load prevented)");
                    Log.Screen("co-op sync patches verified — client bootstrap fixed");
                }
                return false; // skip original; verification forced to succeed
            }
            catch (Exception ex)
            {
                Log.Info("[CLIENT-FIX] verify prefix error (passing through to original): " + ex.Message);
                return true;
            }
        }

        /// <summary>Decision-logic self-test: every reflection target the fix depends on
        /// must have resolved (the thing most likely to break when BT updates), and the
        /// self-disable probe must be callable without throwing. Proves the fix's wiring
        /// is intact independent of the live game reaching the bootstrap path.</summary>
        private static SelfHealing.TestResult SelfTest()
        {
            bool wiring = _createMethod != null && _indexProp != null && _getActionCodeWithName != null
                && _getNumActionCodes != null && _getNumAnimations != null && _isAnyAnimationLoadingFromDisk != null
                && _verifiedField != null;
            bool probeOk;
            try { MirrorsAlreadyPrimed(); probeOk = true; } catch { probeOk = false; }
            bool pass = wiring && probeOk;
            return SelfHealing.TestResult.Of("client-bootstrap-fix.wiring", pass,
                pass ? "all reflection targets resolved; self-disable probe callable"
                     : "MISSING targets (BT updated?) wiring=" + wiring + " probe=" + probeOk);
        }

        private static int ActionCode(string name)
        {
            object value = _getActionCodeWithName.Invoke(null, new object[] { name });
            return value is int ? (int)value : -1;
        }

        /// <summary>Probe for the self-disable path: is the action-cache mirror sentinel
        /// already primed? If so the bug is absent (BT/engine handles it) and we stand down.</summary>
        private static bool MirrorsAlreadyPrimed()
        {
            try
            {
                FieldInfo sentinel = _actionCacheType.GetField("act_inventory_idle_start",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (sentinel == null)
                {
                    return false; // can't tell — treat as bug-present (safe: we still gate on NativeCatalogReady)
                }
                object value = sentinel.GetValue(null);
                if (value == null)
                {
                    return false;
                }
                return (int)_indexProp.GetValue(value, null) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Their exact readiness gate, reproduced so we never force-pass before
        /// the native animation catalog is genuinely loaded.</summary>
        private static bool NativeCatalogReady()
        {
            try
            {
                // All four members are guaranteed non-null (ResolveEngineTypesOnce
                // requires them), so this reproduces their gate exactly — no check is
                // silently skipped.
                if ((int)_getNumActionCodes.Invoke(null, null) <= 0)
                {
                    return false;
                }
                if ((int)_getNumAnimations.Invoke(null, null) <= 0)
                {
                    return false;
                }
                if (ActionCode("act_inventory_idle_start") < 0 ||
                    ActionCode("act_inventory_idle") < 0 ||
                    ActionCode("act_command_leftstance") < 0 ||
                    ActionCode("act_walk_idle_1h_with_shield_left_stance") < 0)
                {
                    return false;
                }
                if ((bool)_isAnyAnimationLoadingFromDisk.Invoke(null, null))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Re-read every static ActionIndexCache mirror from the live catalog so
        /// their audit (mirror vs fresh) matches. Best-effort per field.</summary>
        private static int PrimeActionIndexCacheMirrors()
        {
            int primed = 0;
            try
            {
                foreach (FieldInfo field in _actionCacheType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(f => f.IsStatic && f.FieldType == _actionCacheType))
                {
                    if (field.Name == "act_none")
                    {
                        continue;
                    }
                    try
                    {
                        object current = field.GetValue(null);
                        if (current == null)
                        {
                            continue;
                        }
                        int index = (int)_indexProp.GetValue(current, null);
                        if (index >= 0)
                        {
                            continue; // already primed
                        }
                        object fresh = _createMethod.Invoke(null, new object[] { field.Name });
                        field.SetValue(null, fresh);
                        primed++;
                    }
                    catch
                    {
                        // readonly/inaccessible field — skip; force-verify still carries the fix
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[CLIENT-FIX] mirror prime error: " + ex.Message);
            }
            return primed;
        }
    }
}
