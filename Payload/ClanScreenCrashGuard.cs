using System;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards the community-reported "crash when clicking the clan tab (especially after
    /// becoming a mercenary)" in BannerlordTogether co-op. The clan screen's
    /// GauntletClanScreen.CreateDataSource builds ClanManagementVM over the clan/party
    /// graph, which on a client can be half-synced or hold host-mirrored values, producing
    /// an NRE that CTDs when the screen opens. (BannerlordTogether uses the same
    /// finalizer/preflight pattern for the kingdom screen — KingdomArmyUiPreflightPatch.)
    ///
    /// SELF-DISABLING: this is a finalizer — it does nothing unless CreateDataSource throws.
    /// If BT/TaleWorlds fixes the underlying sync/null bug, the exception stops occurring and
    /// this guard is permanently inert (visible as never-fired in the health report, so it
    /// can be retired). On an escaping exception it logs + closes the screen safely instead
    /// of crashing.
    /// </summary>
    internal static class ClanScreenCrashGuard
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type screen = AccessTools.TypeByName("SandBox.GauntletUI.GauntletClanScreen");
                MethodInfoWrap create = new MethodInfoWrap(screen, "CreateDataSource");
                if (create.Method == null)
                {
                    Log.Info("[CLAN-GUARD] GauntletClanScreen.CreateDataSource not found — guard inactive");
                    Diag.Report("clan-screen-guard", false, "clan screen method not found");
                    return;
                }
                harmony.Patch(create.Method, null, null, null, new HarmonyMethod(typeof(ClanScreenCrashGuard), nameof(CreateDataSourceFinalizer)));
                Log.Info("[CLAN-GUARD] clan-screen crash guard active (self-disables if BT fixes the underlying NRE)");
                Diag.Report("clan-screen-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CLAN-GUARD] apply failed: " + ex.Message);
                Diag.Report("clan-screen-guard", false, ex.Message);
            }
        }

        private static Exception CreateDataSourceFinalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null; // no bug present this time — guard is inert
            }
            SelfHealing.RecordFire("clan-screen-guard");
            Log.Info("[CLAN-GUARD] SUPPRESSED crash in GauntletClanScreen.CreateDataSource (half-synced clan/party): " + __exception.Message);
            Log.Screen("prevented a clan-screen crash (co-op half-sync) — the clan screen was closed");
            try
            {
                // The screen can't build its data source; pop back to the map instead of a CTD.
                var mgr = AccessTools.TypeByName("TaleWorlds.ScreenSystem.ScreenManager");
                var pop = mgr != null ? AccessTools.Method(mgr, "PopScreen") : null;
                pop?.Invoke(null, null);
            }
            catch
            {
            }
            return null; // swallow — do not crash to desktop
        }

        private static SelfHealing.TestResult SelfTest()
        {
            // Prove the finalizer's decision: null exception => inert (return null, no fire);
            // non-null => suppress (return null). We verify the target method still exists
            // (the thing that breaks on a game/BT update) and that the finalizer contract holds.
            Type screen = AccessTools.TypeByName("SandBox.GauntletUI.GauntletClanScreen");
            bool methodExists = screen != null && AccessTools.Method(screen, "CreateDataSource") != null;
            Exception passthrough = CreateDataSourceFinalizer(null); // must be inert on null
            bool inertOnNull = passthrough == null;
            bool pass = methodExists && inertOnNull;
            return SelfHealing.TestResult.Of("clan-screen-guard.contract", pass,
                pass ? "target method present; finalizer inert on null exception"
                     : "methodExists=" + methodExists + " inertOnNull=" + inertOnNull + " (game/BT changed?)");
        }

        /// <summary>Small helper so a null screen type doesn't throw during AccessTools.Method.</summary>
        private struct MethodInfoWrap
        {
            public readonly System.Reflection.MethodInfo Method;
            public MethodInfoWrap(Type type, string name)
            {
                Method = type != null ? AccessTools.Method(type, name) : null;
            }
        }
    }
}
