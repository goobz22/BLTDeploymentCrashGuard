using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only diagnostic tracer. Never changes behavior — every hook is a void
    /// prefix/postfix that appends a [TRACE] line to CrashGuard.log. Purpose: when a
    /// mission scene opens or a map menu switches unexpectedly (e.g. a village raid
    /// dropping into a 3D scene), the log shows WHICH method fired and WHO called it,
    /// including Harmony-patched callers (they appear as DMD&lt;...&gt; frames naming the
    /// original patched method).
    ///
    /// Chokepoints traced:
    ///  - MissionState.OpenNew            → every 3D mission launch, with caller stack
    ///  - GameMenu.ActivateGameMenu       → map menu opens, with caller stack
    ///  - GameMenu.SwitchToMenu           → map menu switches, with caller stack
    ///  - EncounterManager.StartSettlementEncounter → settlement encounters
    ///  - PlayerEncounter.StartBattle / Finish      → player encounter lifecycle
    ///  - DefaultEncounterGameMenuModel.GetGenericStateMenu → logged only on change
    ///    (BannerlordTogether's AutoWaitMenuPatch prefixes this; our postfix logs the
    ///    final value it produced either way)
    /// </summary>
    internal static class TracePatches
    {
        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            applied += PatchByName(harmony, typeof(MissionState), "OpenNew", nameof(MissionOpenNewPrefix), null);
            applied += PatchByName(harmony, typeof(GameMenu), "ActivateGameMenu", nameof(MenuActivatePrefix), null);
            applied += PatchByName(harmony, typeof(GameMenu), "SwitchToMenu", nameof(MenuSwitchPrefix), null);
            applied += PatchByName(harmony, typeof(EncounterManager), "StartSettlementEncounter", nameof(SettlementEncounterPrefix), null);
            applied += PatchByName(harmony, typeof(EncounterManager), "StartPartyEncounter", nameof(PartyEncounterPrefix), null);
            applied += PatchByName(harmony, typeof(TaleWorlds.CampaignSystem.MapEvents.MapEvent), "CanPartyJoinBattle", null, nameof(CanJoinBattlePostfix));
            applied += PatchByName(harmony, typeof(PlayerEncounter), "StartBattle", nameof(EncounterStartBattlePrefix), null);
            applied += PatchByName(harmony, typeof(PlayerEncounter), "Finish", nameof(EncounterFinishPrefix), null);
            applied += PatchByName(harmony, typeof(DefaultEncounterGameMenuModel), "GetGenericStateMenu", null, nameof(StateMenuPostfix));
            Log.Info("[TRACE] tracer active on " + applied + " method overload(s)");
        }

        private static int PatchByName(Harmony harmony, Type type, string methodName, string prefixName, string postfixName)
        {
            int count = 0;
            try
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(TracePatches), prefixName) : null;
                        HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(TracePatches), postfixName) : null;
                        harmony.Patch(method, prefix, postfix);
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[TRACE] could not patch " + type.Name + "." + methodName + ": " + exOne.Message);
                    }
                }
                if (count == 0)
                {
                    Log.Info("[TRACE] no patchable method named " + type.Name + "." + methodName + " found");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[TRACE] patch-by-name failed for " + type.Name + "." + methodName + ": " + ex.Message);
            }
            return count;
        }

        // ---- hooks (all log-only) ----

        private static void MissionOpenNewPrefix(object[] __args)
        {
            // Last-chance mode decision before the mission is built.
            BattleMode.DecideAndApply(PayloadEntry.Harmony, "mission-open");
            Log.Info("[TRACE] >>> MissionState.OpenNew " + FormatArgs(__args) + CallStack());
        }

        private static void MenuActivatePrefix(object[] __args)
        {
            Log.Info("[TRACE] GameMenu.ActivateGameMenu " + FormatArgs(__args) + CallStack());
        }

        private static void MenuSwitchPrefix(object[] __args)
        {
            Log.Info("[TRACE] GameMenu.SwitchToMenu " + FormatArgs(__args) + CallStack());
        }

        private static void SettlementEncounterPrefix(object[] __args)
        {
            // AI parties enter settlements constantly; only the player's encounters are
            // diagnostic signal (the AI flood drowned the log on 2026-08-18).
            try
            {
                bool involvesMainParty = false;
                if (__args != null)
                {
                    foreach (object arg in __args)
                    {
                        TaleWorlds.CampaignSystem.Party.MobileParty party = arg as TaleWorlds.CampaignSystem.Party.MobileParty;
                        if (party != null && party.IsMainParty)
                        {
                            involvesMainParty = true;
                            break;
                        }
                    }
                }
                if (!involvesMainParty)
                {
                    return;
                }
            }
            catch
            {
            }
            Log.Info("[TRACE] EncounterManager.StartSettlementEncounter " + FormatArgs(__args) + CallStack());
        }

        private static void PartyEncounterPrefix(object[] __args)
        {
            if (!InvolvesMainParty(__args))
            {
                return;
            }
            Log.Info("[TRACE] EncounterManager.StartPartyEncounter " + FormatArgs(__args) + CallStack());
        }

        private static void CanJoinBattlePostfix(object[] __args, bool __result)
        {
            if (!InvolvesMainParty(__args))
            {
                return;
            }
            Log.Info("[TRACE] MapEvent.CanPartyJoinBattle " + FormatArgs(__args) + " -> " + __result + CallStack());
        }

        private static bool InvolvesMainParty(object[] args)
        {
            try
            {
                if (args == null)
                {
                    return false;
                }
                foreach (object arg in args)
                {
                    TaleWorlds.CampaignSystem.Party.MobileParty mobile = arg as TaleWorlds.CampaignSystem.Party.MobileParty;
                    if (mobile != null && mobile.IsMainParty)
                    {
                        return true;
                    }
                    TaleWorlds.CampaignSystem.Party.PartyBase partyBase = arg as TaleWorlds.CampaignSystem.Party.PartyBase;
                    if (partyBase != null && partyBase.MobileParty != null && partyBase.MobileParty.IsMainParty)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void EncounterStartBattlePrefix()
        {
            BattleMode.DecideAndApply(PayloadEntry.Harmony, "start-battle");
            Log.Info("[TRACE] PlayerEncounter.StartBattle" + CallStack());
        }

        private static void EncounterFinishPrefix(object[] __args)
        {
            EncounterLoopGuard.NoteEncounterFinish();
            Log.Info("[TRACE] PlayerEncounter.Finish " + FormatArgs(__args) + CallStack());
        }

        private static string _lastStateMenu = "<unset>";

        private static void StateMenuPostfix(string __result)
        {
            string value = __result ?? "(null)";
            if (value == _lastStateMenu)
            {
                return;
            }
            _lastStateMenu = value;
            Log.Info("[TRACE] GetGenericStateMenu changed -> " + value);
        }

        // ---- helpers ----

        private static string FormatArgs(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "()";
            }
            StringBuilder sb = new StringBuilder("(");
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                object arg = args[i];
                if (arg == null)
                {
                    sb.Append("null");
                    continue;
                }
                try
                {
                    string text = arg.ToString();
                    if (text != null && text.Length > 80)
                    {
                        text = text.Substring(0, 80) + "…";
                    }
                    sb.Append(text);
                }
                catch
                {
                    sb.Append('<').Append(arg.GetType().Name).Append('>');
                }
            }
            return sb.Append(')').ToString();
        }

        private static string CallStack()
        {
            try
            {
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null)
                {
                    return " (no stack)";
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
                        // Harmony-patched callers show up as dynamic methods named
                        // "DMD<Namespace.Type::Method>" — keep them, they identify the
                        // original patched method that made this call.
                        sb.Append("\n      at ").Append(method.Name);
                    }
                    if (++shown >= 20)
                    {
                        break;
                    }
                }
                return sb.Length > 0 ? sb.ToString() : " (stack empty)";
            }
            catch (Exception ex)
            {
                return " (stack error: " + ex.Message + ")";
            }
        }
    }
}
