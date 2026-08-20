using System;
using System.Collections.Generic;
using System.IO;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Version/build identity, per-launch session id, and the startup HEALTH SUMMARY. In the
    /// harness so the session id survives payload reloads; the health lists are cleared per
    /// generation (ResetHealth, called by the reload engine before each payload Apply) so a
    /// reload doesn't duplicate entries.
    /// </summary>
    internal static class Diag
    {
        internal const string Version = "1.2.0";

        internal static readonly string SessionId = GenerateSessionId();

        private static readonly List<string> _healthy = new List<string>();
        private static readonly List<string> _degraded = new List<string>();
        private static bool _criticalMissing;

        private static string GenerateSessionId()
        {
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            return (Environment.TickCount ^ (pid << 8)).ToString("x8");
        }

        internal static string BuildTime()
        {
            try
            {
                // The generation banner uses the PAYLOAD build time; the harness time is stable.
                return File.GetLastWriteTime(typeof(Diag).Assembly.Location).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unknown";
            }
        }

        internal static string Banner()
        {
            return "===== BLT Deployment Crash Guard v" + Version + " (harness build " + BuildTime() + ") session=" + SessionId + " =====";
        }

        /// <summary>Clear per-generation health so a reload starts fresh.</summary>
        internal static void ResetHealth()
        {
            _healthy.Clear();
            _degraded.Clear();
            _criticalMissing = false;
        }

        internal static void Report(string component, bool ok, string detail, bool critical = false)
        {
            if (ok)
            {
                _healthy.Add(component);
            }
            else
            {
                _degraded.Add(component + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                if (critical)
                {
                    _criticalMissing = true;
                }
            }
        }

        internal static string HealthSummary()
        {
            string summary = "MOD HEALTH: " + _healthy.Count + " active";
            if (_degraded.Count > 0)
            {
                summary += ", " + _degraded.Count + " NOT resolved -> " + string.Join("; ", _degraded.ToArray()) +
                           "  (likely a BannerlordTogether update renamed a method — check for a mod update)";
                if (_criticalMissing)
                {
                    Log.Screen("WARNING: a core BLT-guard fix did not load (BT may have updated) — see CrashGuard.log");
                }
            }
            else
            {
                summary += ", all resolved";
            }
            return summary;
        }
    }
}
