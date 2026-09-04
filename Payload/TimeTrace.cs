using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only tracer for CAMPAIGN TIME CONTROL. Symptom: clicking things on the map
    /// (e.g. a city) sometimes drops fast-forward when only the pause/play buttons
    /// should change speed. Every mode change is logged with old -> new and the full
    /// calling stack, so the code path forcing the change is named directly.
    ///
    /// Hooks:
    ///  - Campaign.set_TimeControlMode      → old -> new + stack (only when it changes);
    ///    a postfix flags when the requested change was suppressed/altered by another
    ///    patch (their prefix runs too — all Harmony prefixes execute).
    ///  - Campaign.SetTimeControlModeLock / set_TimeControlModeLock (whichever exists)
    ///  - MapTimeControlVM.ExecuteTimeControlChange → marks genuine UI button clicks,
    ///    so button-driven changes are distinguishable from code-driven ones.
    /// </summary>
    internal static class TimeTrace
    {
        [ThreadStatic]
        private static CampaignTimeControlMode _pendingOldMode;
        [ThreadStatic]
        private static CampaignTimeControlMode _pendingNewMode;
        [ThreadStatic]
        private static string _pendingStack;
        [ThreadStatic]
        private static bool _pendingLogged;

        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            applied += PatchByName(harmony, "TaleWorlds.CampaignSystem.Campaign", "set_TimeControlMode", nameof(SetModePrefix), nameof(SetModePostfix));
            applied += PatchByName(harmony, "TaleWorlds.CampaignSystem.Campaign", "SetTimeControlModeLock", nameof(LockPrefix), null);
            applied += PatchByName(harmony, "TaleWorlds.CampaignSystem.Campaign", "set_TimeControlModeLock", nameof(LockPrefix), null);
            applied += PatchByName(harmony, "TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar.MapTimeControlVM", "ExecuteTimeControlChange", nameof(UiButtonPrefix), null);
            Log.Info("[TIME] time-control tracer active on " + applied + " method(s)");
        }

        private static int PatchByName(Harmony harmony, string typeName, string methodName, string prefixName, string postfixName)
        {
            int count = 0;
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    return 0;
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(TimeTrace), prefixName) : null;
                        HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(TimeTrace), postfixName) : null;
                        harmony.Patch(method, prefix, postfix);
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[TIME] could not patch " + typeName + "." + methodName + ": " + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[TIME] patch-by-name failed for " + typeName + "." + methodName + ": " + ex.Message);
            }
            return count;
        }

        // ---- hooks ----

        private static void SetModePrefix(Campaign __instance, CampaignTimeControlMode value)
        {
            try
            {
                _pendingLogged = false;
                if (__instance == null || __instance.TimeControlMode == value)
                {
                    return; // no-op sets are noise
                }
                // Capture only — the full line (with stack) is emitted in the postfix once the
                // actual outcome is known, and routed through TraceThrottle so a request that
                // repeats every tick (e.g. BT's EnforcePlaySpeed while our guard blocks it)
                // logs one full line + a periodic count instead of ~60 lines/second.
                _pendingOldMode = __instance.TimeControlMode;
                _pendingNewMode = value;
                _pendingStack = Stack();
                _pendingLogged = true;
            }
            catch
            {
            }
        }

        private static void SetModePostfix(Campaign __instance)
        {
            try
            {
                if (!_pendingLogged || __instance == null)
                {
                    return;
                }
                _pendingLogged = false;
                bool suppressed = __instance.TimeControlMode != _pendingNewMode;
                string message = "[TIME] TimeControlMode " + _pendingOldMode + " -> " + _pendingNewMode + _pendingStack;
                if (suppressed)
                {
                    message += "\n[TIME]   ^ change SUPPRESSED/ALTERED by another patch — actual mode now " + __instance.TimeControlMode;
                }
                // Dedup key ignores the (identical) stack: a request that repeats collapses.
                string key = "TIME " + _pendingOldMode + "->" + _pendingNewMode + (suppressed ? " SUPPRESSED->" + __instance.TimeControlMode : " applied");
                TraceThrottle.Emit(key, message);
            }
            catch
            {
            }
        }

        private static void LockPrefix(object[] __args)
        {
            try
            {
                StringBuilder sb = new StringBuilder("[TIME] TimeControlModeLock(");
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(__args[i] != null ? __args[i].ToString() : "null");
                    }
                }
                sb.Append(')');
                Log.Info(sb + Stack());
            }
            catch
            {
            }
        }

        private static void UiButtonPrefix(object[] __args)
        {
            try
            {
                string arg = __args != null && __args.Length > 0 && __args[0] != null ? __args[0].ToString() : "?";
                Log.Info("[TIME] UI time button clicked: ExecuteTimeControlChange(" + arg + ")");
            }
            catch
            {
            }
        }

        private static string Stack()
        {
            try
            {
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null)
                {
                    return "";
                }
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }
                    Type declaring = method.DeclaringType;
                    string typeName = declaring != null ? declaring.FullName : null;
                    if (typeName != null)
                    {
                        if (typeName.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                            typeName.StartsWith("BLTDeploymentCrashGuard", StringComparison.Ordinal) ||
                            typeName.StartsWith("System.", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        sb.Append("\n      at ").Append(typeName).Append('.').Append(method.Name);
                    }
                    else
                    {
                        // DMD<...> dynamic-method frames name the original patched caller
                        sb.Append("\n      at ").Append(method.Name);
                    }
                    if (++shown >= 14)
                    {
                        break;
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }
    }
}
