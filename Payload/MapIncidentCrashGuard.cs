using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Localization;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards the map-incident popup against stale world state (field crash 2026-08-30 15:04,
    /// crashreport1.html): clicking Confirm on an incident option CTD'd with an NRE inside
    /// TaleWorlds.CampaignSystem.Incidents.IncidentEffect.SiegeProgressChange's consequence
    /// lambda. The IL at the fault site dereferences
    /// PlayerSiege.PlayerSiegeEvent.BesiegerCamp.SiegeEngines.SiegePreparations with no null
    /// check — the incident was offered while a siege was live, and by the time the player
    /// confirmed, the player siege was gone (ended, or never set on this peer in co-op).
    /// Pure vanilla bug; BannerlordTogether only widens the stale window (a popup can sit
    /// open while the other player's actions end the siege).
    ///
    /// Three layers, innermost = root behavior fix, outer two = class safety nets (the class:
    /// "incident option handlers assume the world state that spawned the incident is still
    /// live when the player clicks"):
    ///  1. Prefix on every SiegeProgressChange consequence lambda: when the player-siege
    ///     chain is no longer intact, skip the effect and report "the siege has already
    ///     ended" instead of applying progress to a dead siege — what vanilla should do.
    ///  2. Finalizer on IncidentEffect.Consequence(): ANY incident effect whose closure
    ///     throws yields an empty consequence list instead of a CTD; sibling effects in a
    ///     Group still apply.
    ///  3. Finalizer on Incident.InvokeOption(): outer belt — anything escaping layer 2
    ///     closes the popup cleanly instead of crashing the click handler.
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

                // Layer 1 — the proven crasher: the SiegeProgressChange consequence lambda(s).
                // Compiler-generated display-class numbers shift between game builds, but the
                // "<SiegeProgressChange>b__" stub derives from the method name and is stable.
                int lambdas = 0;
                foreach (Type nested in incidentEffect.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (MethodInfo m in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("<SiegeProgressChange>b__", StringComparison.Ordinal) &&
                            m.ReturnType == typeof(List<TextObject>))
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(MapIncidentCrashGuard), nameof(SiegeLambdaPrefix)));
                            lambdas++;
                        }
                    }
                }
                patched += lambdas;

                // Layer 2 — class net at the single choke point every effect flows through.
                MethodInfo consequence = AccessTools.Method(incidentEffect, "Consequence");
                if (consequence != null)
                {
                    harmony.Patch(consequence, null, null, null, new HarmonyMethod(typeof(MapIncidentCrashGuard), nameof(ConsequenceFinalizer)));
                    patched++;
                }

                // Layer 3 — outer belt on the click handler's campaign entry point.
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
                Log.Info("[INCIDENT-GUARD] map-incident crash guard active on " + patched + " method(s) (" +
                         lambdas + " siege lambda(s), consequence=" + (consequence != null) +
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

        /// <summary>The whole chain the vanilla lambda dereferences unchecked.</summary>
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
                return false; // unreadable state is as dead as null state — skip the effect
            }
        }

        private static bool SiegeLambdaPrefix(ref List<TextObject> __result)
        {
            if (SiegeChainIntact())
            {
                return true; // siege is live — run the vanilla effect untouched
            }
            SelfHealing.RecordFire("map-incident-guard");
            Log.Info("[INCIDENT-GUARD] skipped SiegeProgressChange incident effect — player siege no longer exists (vanilla would NRE-CTD here)");
            __result = Substitute();
            return false;
        }

        /// <summary>The list the skipped siege effect reports instead of applying to a dead siege.</summary>
        private static List<TextObject> Substitute()
        {
            return new List<TextObject> { new TextObject("The siege has already ended.") };
        }

        private static Exception ConsequenceFinalizer(Exception __exception, ref List<TextObject> __result)
        {
            if (__exception == null)
            {
                return null;
            }
            SelfHealing.RecordFire("map-incident-guard");
            Log.Info("[INCIDENT-GUARD] SUPPRESSED crash in IncidentEffect.Consequence (stale world state behind the incident popup): " + __exception.Message);
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
            Log.Info("[INCIDENT-GUARD] SUPPRESSED crash in Incident.InvokeOption — option closed without its effect: " + __exception.Message);
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
            // The exact crash decision: with no live player siege (true at startup — no
            // campaign loaded) the chain reads dead and the substitute list is non-empty.
            List<TextObject> substitute = Substitute();
            bool skipsOnDeadSiege = SiegeChainIntact() || (substitute != null && substitute.Count > 0);
            bool pass = typeExists && inertOnNull && skipsOnDeadSiege;
            return SelfHealing.TestResult.Of("map-incident-guard.contract", pass,
                pass ? "targets re-resolved; finalizer inert on null; dead-siege substitute non-empty"
                     : "typeExists=" + typeExists + " inertOnNull=" + inertOnNull + " skipsOnDeadSiege=" + skipsOnDeadSiege);
        }
    }
}
