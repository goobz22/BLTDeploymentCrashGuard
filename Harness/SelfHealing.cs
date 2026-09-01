using System;
using System.Collections.Generic;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Makes our patches self-aware so they naturally deactivate when BannerlordTogether
    /// (or TaleWorlds) fixes the underlying bug — and provable via in-code self-tests.
    ///
    /// Two mechanisms:
    ///  1. FIRE TRACKING. Every guard reports each time it actually suppresses a crash or
    ///     corrects state (RecordFire). A crash-guard finalizer that never fires across a
    ///     session did nothing — i.e. the bug it guards no longer occurs. The startup/health
    ///     report lists which guards fired and which stayed inert, so a bug that upstream
    ///     fixed shows up as a permanently-inert guard (safe to retire).
    ///  2. PROBES. A behavior patch (not a crash finalizer) that would override upstream even
    ///     after a fix must ask "is the bug still present?" before acting. ClientBootstrapFix
    ///     probes whether the action-cache mirrors are already primed (BT fixed it) and, if
    ///     so, stands down. Register such probes here so the health report shows them.
    ///
    /// SELF-TESTS: guards register a decision-logic test (name + a predicate returning
    /// pass/detail). With "selfTest": true in guardconfig.json they run at startup and log
    /// PASS/FAIL — runnable proof the suppression/probe logic is correct (matches the bug
    /// signature, rejects unrelated input), independent of the live game hitting the path.
    /// </summary>
    public static class SelfHealing
    {
        private static readonly Dictionary<string, int> Fires = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<Func<TestResult>> Tests = new List<Func<TestResult>>();
        private static readonly object Sync = new object();

        public struct TestResult
        {
            public string Name;
            public bool Pass;
            public string Detail;
            public static TestResult Of(string name, bool pass, string detail)
            {
                return new TestResult { Name = name, Pass = pass, Detail = detail };
            }
        }

        /// <summary>A guard suppressed a crash / corrected state once. Cheap; call every fire.</summary>
        public static void RecordFire(string guard)
        {
            try
            {
                lock (Sync)
                {
                    int n;
                    Fires[guard] = Fires.TryGetValue(guard, out n) ? n + 1 : 1;
                }
            }
            catch
            {
            }
        }

        public static string FireSummary()
        {
            try
            {
                lock (Sync)
                {
                    if (Fires.Count == 0)
                    {
                        return "GUARD ACTIVITY: none fired this session (nothing crashed on a guarded path)";
                    }
                    List<string> parts = new List<string>();
                    foreach (KeyValuePair<string, int> kv in Fires)
                    {
                        parts.Add(kv.Key + "=" + kv.Value);
                    }
                    return "GUARD ACTIVITY: " + string.Join(", ", parts.ToArray());
                }
            }
            catch
            {
                return "GUARD ACTIVITY: (unavailable)";
            }
        }

        public static void RegisterTest(Func<TestResult> test)
        {
            try
            {
                Tests.Add(test);
            }
            catch
            {
            }
        }

        /// <summary>Cleared by the reload engine before each payload generation applies, so
        /// reloads don't accumulate duplicate self-tests (fire counts are kept — they persist
        /// across reloads to prove shared state survived).</summary>
        public static void ResetTests()
        {
            try
            {
                Tests.Clear();
            }
            catch
            {
            }
        }

        /// <summary>Run all registered decision-logic self-tests; log each and a summary.
        /// Called at startup only when guardconfig selfTest=true.</summary>
        public static void RunSelfTests()
        {
            int pass = 0, fail = 0;
            Log.Info("[SELFTEST] running " + Tests.Count + " guard decision-logic test(s)…");
            foreach (Func<TestResult> test in Tests)
            {
                TestResult r;
                try
                {
                    r = test();
                }
                catch (Exception ex)
                {
                    r = TestResult.Of("(threw)", false, ex.Message);
                }
                if (r.Pass)
                {
                    pass++;
                    Log.Info("[SELFTEST] PASS " + r.Name + (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail));
                }
                else
                {
                    fail++;
                    Log.Info("[SELFTEST] FAIL " + r.Name + " — " + r.Detail);
                }
            }
            Log.Info("[SELFTEST] " + pass + " passed, " + fail + " failed");
            if (fail > 0)
            {
                Log.Screen("self-tests: " + fail + " FAILED (see CrashGuard.log)");
            }
        }
    }
}
