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
    public static class Diag
    {
        /// <summary>Single source of truth is &lt;Version&gt; in Directory.Build.props — MSBuild
        /// stamps it into the assembly identity, and this reads it back. Never hardcode.</summary>
        public static readonly string Version = ResolveVersion();

        private static string ResolveVersion()
        {
            try
            {
                System.Version v = typeof(Diag).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch
            {
                return "?";
            }
        }

        public static readonly string SessionId = GenerateSessionId();

        private static readonly List<string> _healthy = new List<string>();
        private static readonly List<string> _degradedIds = new List<string>();
        private static readonly List<string> _degraded = new List<string>(); // "id (detail)" display strings, parallel to _degradedIds
        private static readonly List<string> _criticalIds = new List<string>();

        private static string GenerateSessionId()
        {
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            return (Environment.TickCount ^ (pid << 8)).ToString("x8");
        }

        public static string BuildTime()
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

        public static string Banner()
        {
            return "===== BLT Deployment Crash Guard v" + Version + " (harness build " + BuildTime() + ") session=" + SessionId + " =====";
        }

        /// <summary>Clear per-generation health so a reload starts fresh.</summary>
        public static void ResetHealth()
        {
            _healthy.Clear();
            _degradedIds.Clear();
            _degraded.Clear();
            _criticalIds.Clear();
        }

        /// <summary>Keyed by component: the LATEST report for an id wins. A guard that first reported
        /// "inert — BannerlordTogether not loaded" at payload apply and then resolved on the
        /// module-screen / game-start retry replaces its entry instead of appearing twice (2026-09-04).</summary>
        public static void Report(string component, bool ok, string detail, bool critical = false)
        {
            Forget(component);
            if (ok)
            {
                _healthy.Add(component);
            }
            else
            {
                _degradedIds.Add(component);
                _degraded.Add(component + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                if (critical)
                {
                    _criticalIds.Add(component);
                }
            }
        }

        private static void Forget(string component)
        {
            _healthy.Remove(component);
            int index = _degradedIds.IndexOf(component);
            if (index >= 0)
            {
                _degradedIds.RemoveAt(index);
                _degraded.RemoveAt(index);
            }
            _criticalIds.Remove(component);
        }

        public static string HealthSummary()
        {
            string summary = "MOD HEALTH: " + _healthy.Count + " active";
            if (_degraded.Count > 0)
            {
                summary += ", " + _degraded.Count + " NOT resolved -> " + string.Join("; ", _degraded.ToArray()) +
                           "  (read each detail: a BannerlordTogether OR game update may have renamed a member; a detail saying 'inert', 'not loaded' or 'older game build' is on purpose)";
                if (_criticalIds.Count > 0)
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
