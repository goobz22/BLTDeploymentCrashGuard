using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Fixes the map-incident popup CTD (field crash 2026-08-30 15:04, crashreport1.html):
    /// clicking Confirm on an incident option NREs inside
    /// TaleWorlds.CampaignSystem.Incidents.IncidentEffect.SiegeProgressChange's consequence
    /// lambda, which dereferences PlayerSiege.PlayerSiegeEvent.BesiegerCamp.SiegeEngines.
    /// SiegePreparations with no null check.
    ///
    /// Root analysis (probed against the installed build, not assumed):
    /// PlayerSiege.PlayerSiegeEvent is a COMPUTED getter = MainParty.SiegeEvent ??
    /// MainParty.CurrentSettlement?.SiegeEvent — there is no settable mirror. It reads null in
    /// two distinct situations, which get two distinct treatments (never a feature downgrade):
    ///  - CO-OP ATTACH GAP: the player rides in an army that is besieging, but BT never
    ///    attached this peer's party to the besieger camp, so the derivation chain is dead
    ///    while the army's siege is LIVE. REPAIR: find the real siege through the army
    ///    (AttachedTo / Army.LeaderParty) and apply the exact vanilla effect to it —
    ///    SetProgress(Progress + amount) + the same {=C0kUpB48} report text — so co-op keeps
    ///    the full incident, identical to what a solo player gets.
    ///  - SIEGE GENUINELY OVER (reproducible in pure vanilla singleplayer: the popup sits open
    ///    while the siege ends): no siege exists anywhere to receive progress; the effect
    ///    reports "the siege has already ended" — the behavior vanilla itself should have.
    ///
    /// Patch selection is by IL inspection, not lambda numbering: only SiegeProgressChange
    /// lambdas that actually call PlayerSiege.get_PlayerSiegeEvent are patched (b__1, the
    /// consequence). The preview-text lambda (b__2) never touches the siege and is left alone.
    ///
    /// Class safety nets (the class: "incident option handlers assume the world state that
    /// spawned the incident is still live on confirm"): finalizers on
    /// IncidentEffect.Consequence() and Incident.InvokeOption() turn any OTHER stale-state
    /// throw into a logged, fire-counted skip instead of a CTD — each fire is evidence for
    /// the next root fix, per this mod's fire-tracking contract.
    /// All targets resolve by name (the Incidents namespace is new); health-reported.
    /// </summary>
    internal static class MapIncidentCrashGuard
    {
        private static bool _applied;

        internal static void Apply(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                Type incidentEffect = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Incidents.IncidentEffect");
                if (incidentEffect == null)
                {
                    Log.Info("[INCIDENT-GUARD] IncidentEffect not found — guard inactive (older game build without map incidents)");
                    Diag.Report("map-incident-guard", false, "IncidentEffect type not found");
                    return;
                }

                int patched = 0;

                // Root fix — exactly the lambda(s) that dereference the player-siege chain.
                int lambdas = 0;
                foreach (Type nested in incidentEffect.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (MethodInfo m in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("<SiegeProgressChange>b__", StringComparison.Ordinal) &&
                            m.ReturnType == typeof(List<TextObject>) &&
                            CallsPlayerSiegeGetter(m))
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(MapIncidentCrashGuard), nameof(SiegeConsequencePrefix)));
                            lambdas++;
                        }
                    }
                }
                patched += lambdas;

                // Class net at the single choke point every incident effect flows through.
                MethodInfo consequence = AccessTools.Method(incidentEffect, "Consequence");
                if (consequence != null)
                {
                    harmony.Patch(consequence, null, null, null, new HarmonyMethod(typeof(MapIncidentCrashGuard), nameof(ConsequenceFinalizer)));
                    patched++;
                }

                // Outer belt on the click handler's campaign entry point.
                Type incident = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Incidents.Incident");
                MethodInfo invokeOption = incident != null ? AccessTools.Method(incident, "InvokeOption") : null;
                if (invokeOption != null && invokeOption.ReturnType == typeof(List<TextObject>))
                {
                    harmony.Patch(invokeOption, null, null, null, new HarmonyMethod(typeof(MapIncidentCrashGuard), nameof(InvokeOptionFinalizer)));
                    patched++;
                }

                if (patched == 0)
                {
                    Log.Info("[INCIDENT-GUARD] no incident methods resolved — guard inactive (game update?)");
                    Diag.Report("map-incident-guard", false, "no methods resolved");
                    return;
                }
                _applied = true;
                Log.Info("[INCIDENT-GUARD] map-incident fix active on " + patched + " method(s) (" +
                         lambdas + " siege-consequence lambda(s) by IL inspection, consequence=" + (consequence != null) +
                         ", invokeOption=" + (invokeOption != null) + ")");
                Diag.Report("map-incident-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[INCIDENT-GUARD] apply failed: " + ex.Message);
                Diag.Report("map-incident-guard", false, ex.Message);
            }
        }

        /// <summary>Does this method's IL call PlayerSiege.get_PlayerSiegeEvent? Discriminates
        /// the crashing consequence lambda from the harmless preview lambda without depending
        /// on compiler-generated numbering.</summary>
        private static bool CallsPlayerSiegeGetter(MethodInfo method)
        {
            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null)
                {
                    return false;
                }
                byte[] il = body.GetILAsByteArray();
                for (int i = 0; i < il.Length - 4; i++)
                {
                    if (il[i] == 0x28) // call
                    {
                        try
                        {
                            MemberInfo target = method.Module.ResolveMember(BitConverter.ToInt32(il, i + 1));
                            if (target != null && target.Name == "get_PlayerSiegeEvent" &&
                                target.DeclaringType == typeof(PlayerSiege))
                            {
                                return true;
                            }
                            i += 4;
                        }
                        catch
                        {
                            // not a real call site (opcode byte inside operand data) — keep scanning
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>The chain the vanilla lambda dereferences unchecked.</summary>
        private static bool SiegeChainIntact()
        {
            try
            {
                SiegeEvent siege = PlayerSiege.PlayerSiegeEvent;
                return siege != null &&
                       siege.BesiegerCamp != null &&
                       siege.BesiegerCamp.SiegeEngines != null &&
                       siege.BesiegerCamp.SiegeEngines.SiegePreparations != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The player's REAL siege when the PlayerSiege derivation is dead: in co-op a
        /// peer's party can ride in a besieging army without being attached to the besieger
        /// camp, so vanilla's MainParty-based derivation misses the army's live siege.</summary>
        private static SiegeEvent FindLiveSiegeViaArmy()
        {
            try
            {
                MobileParty main = MobileParty.MainParty;
                if (main == null)
                {
                    return null;
                }
                SiegeEvent[] candidates =
                {
                    main.SiegeEvent,
                    main.CurrentSettlement != null ? main.CurrentSettlement.SiegeEvent : null,
                    main.AttachedTo != null ? main.AttachedTo.SiegeEvent : null,
                    main.Army != null && main.Army.LeaderParty != null ? main.Army.LeaderParty.SiegeEvent : null,
                    main.Army != null && main.Army.LeaderParty != null && main.Army.LeaderParty.CurrentSettlement != null
                        ? main.Army.LeaderParty.CurrentSettlement.SiegeEvent : null
                };
                foreach (SiegeEvent s in candidates)
                {
                    if (s != null && s.BesiegerCamp != null && s.BesiegerCamp.SiegeEngines != null &&
                        s.BesiegerCamp.SiegeEngines.SiegePreparations != null)
                    {
                        return s;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool SiegeConsequencePrefix(object __instance, ref List<TextObject> __result)
        {
            if (SiegeChainIntact())
            {
                return true; // vanilla state is healthy — the real effect runs untouched
            }
            try
            {
                SiegeEvent real = FindLiveSiegeViaArmy();
                if (real != null)
                {
                    float amount = ReadAmount(__instance);
                    SiegeEvent.SiegeEngineConstructionProgress prep = real.BesiegerCamp.SiegeEngines.SiegePreparations;
                    prep.SetProgress(prep.Progress + amount);
                    SelfHealing.RecordFire("map-incident-guard");
                    Log.Info("[INCIDENT-GUARD] REPAIRED siege-progress incident: party not attached to the live siege of " +
                             SiegeName(real) + " (co-op army attach gap — vanilla derivation read null and would NRE-CTD); " +
                             "applied the vanilla effect to the real siege, amount=" + amount);
                    // Vanilla's own report line, verbatim (same localization id as the lambda builds).
                    TextObject text = new TextObject("{=C0kUpB48}{?AMOUNT > 0}Increased{?}Decreased{\\?} siege progress by {ABS(AMOUNT)}%.");
                    text.SetTextVariable("AMOUNT", MathF.Round(amount * 100f));
                    __result = new List<TextObject> { text };
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Info("[INCIDENT-GUARD] repair attempt failed (" + ex.Message + ") — falling back to graceful skip");
            }
            SelfHealing.RecordFire("map-incident-guard");
            Log.Info("[INCIDENT-GUARD] skipped siege-progress incident effect — no live siege anywhere for the player (siege already over; vanilla would NRE-CTD)");
            __result = Substitute();
            return false;
        }

        /// <summary>The effect amount, read from the display-class closure the vanilla lambda
        /// itself uses ("amountGetter" derives from the factory's parameter name).</summary>
        private static float ReadAmount(object displayClass)
        {
            FieldInfo f = displayClass.GetType().GetField("amountGetter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Func<float> getter = f != null ? f.GetValue(displayClass) as Func<float> : null;
            if (getter == null)
            {
                throw new InvalidOperationException("amountGetter closure field not found");
            }
            return getter();
        }

        /// <summary>The list the skipped siege effect reports instead of applying to a dead siege.</summary>
        private static List<TextObject> Substitute()
        {
            return new List<TextObject> { new TextObject("The siege has already ended.") };
        }

        private static string SiegeName(SiegeEvent siege)
        {
            try
            {
                return siege.BesiegedSettlement != null ? siege.BesiegedSettlement.Name.ToString() : "(unknown settlement)";
            }
            catch
            {
                return "(unknown settlement)";
            }
        }

        private static Exception ConsequenceFinalizer(Exception __exception, ref List<TextObject> __result)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("map-incident-guard");
            Log.Info("[INCIDENT-GUARD] SUPPRESSED crash in IncidentEffect.Consequence (stale world state behind the incident popup — root-fix candidate): " + __exception);
            if (__result == null)
            {
                __result = new List<TextObject>();
            }
            return null;
        }

        private static Exception InvokeOptionFinalizer(Exception __exception, ref List<TextObject> __result)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("map-incident-guard");
            Log.Info("[INCIDENT-GUARD] SUPPRESSED crash in Incident.InvokeOption — option closed without its effect (root-fix candidate): " + __exception);
            if (__result == null)
            {
                __result = new List<TextObject>();
            }
            return null;
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type incidentEffect = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Incidents.IncidentEffect");
            bool typeExists = incidentEffect != null && AccessTools.Method(incidentEffect, "Consequence") != null;
            List<TextObject> untouched = null;
            bool inertOnNull = ConsequenceFinalizer(null, ref untouched) == null && untouched == null;
            // The IL discriminator must still find the crashing consequence lambda and must
            // still exclude the siege-free preview lambda (both patterns re-checked live).
            int consequenceLambdas = 0, otherLambdas = 0;
            if (incidentEffect != null)
            {
                foreach (Type nested in incidentEffect.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (MethodInfo m in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("<SiegeProgressChange>b__", StringComparison.Ordinal) &&
                            m.ReturnType == typeof(List<TextObject>))
                        {
                            if (CallsPlayerSiegeGetter(m)) consequenceLambdas++; else otherLambdas++;
                        }
                    }
                }
            }
            bool discriminates = consequenceLambdas >= 1 && otherLambdas >= 1;
            bool pass = typeExists && inertOnNull && discriminates;
            return SelfHealing.TestResult.Of("map-incident-guard.contract", pass,
                pass ? "targets re-resolved; finalizer inert on null; IL discriminator: " + consequenceLambdas + " siege lambda(s) patched, " + otherLambdas + " preview lambda(s) untouched"
                     : "typeExists=" + typeExists + " inertOnNull=" + inertOnNull + " consequenceLambdas=" + consequenceLambdas + " otherLambdas=" + otherLambdas);
        }
    }
}
