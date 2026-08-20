using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;

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
    /// </summary>
    internal static class ClientBootstrapFix
    {
        private static bool _applied;
        private static bool _primeLogged;
        private static FieldInfo _verifiedField;

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
                    return; // co-op mod absent or not loaded yet — Apply is retried later
                }
                MethodInfo verify = AccessTools.Method(coop, "TryVerifyNativeActionCacheWhenCampaignMapReady");
                if (verify == null)
                {
                    Log.Info("[CLIENT-FIX] verify method not found — co-op bootstrap fix INACTIVE (mod version changed?)");
                    return;
                }
                _verifiedField = AccessTools.Field(coop, "_nativeActionCacheVerified");
                harmony.Patch(verify, new HarmonyMethod(typeof(ClientBootstrapFix), nameof(VerifyPrefix)));
                _applied = true;
                Log.Info("[CLIENT-FIX] co-op action-cache bootstrap fix active (prevents client half-load / BootstrapAborted)");
            }
            catch (Exception ex)
            {
                Log.Info("[CLIENT-FIX] apply failed: " + ex.Message);
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
                int primed = PrimeActionIndexCacheMirrors();
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

        /// <summary>Their exact readiness gate, reproduced so we never force-pass before
        /// the native animation catalog is genuinely loaded.</summary>
        private static bool NativeCatalogReady()
        {
            try
            {
                if (MBAnimation.GetNumActionCodes() <= 0 || MBAnimation.GetNumAnimations() <= 0)
                {
                    return false;
                }
                if (MBAnimation.GetActionCodeWithName("act_inventory_idle_start") < 0 ||
                    MBAnimation.GetActionCodeWithName("act_inventory_idle") < 0 ||
                    MBAnimation.GetActionCodeWithName("act_command_leftstance") < 0 ||
                    MBAnimation.GetActionCodeWithName("act_walk_idle_1h_with_shield_left_stance") < 0)
                {
                    return false;
                }
                if (MBAnimation.IsAnyAnimationLoadingFromDisk())
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
                foreach (FieldInfo field in typeof(ActionIndexCache).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(f => f.IsStatic && f.FieldType == typeof(ActionIndexCache)))
                {
                    if (field.Name == "act_none")
                    {
                        continue;
                    }
                    try
                    {
                        object current = field.GetValue(null);
                        if (!(current is ActionIndexCache cache))
                        {
                            continue;
                        }
                        if (cache.Index >= 0)
                        {
                            continue; // already primed
                        }
                        ActionIndexCache fresh = ActionIndexCache.Create(field.Name);
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
