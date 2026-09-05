using System;
using System.IO;
using System.Text;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Detects the co-op mod's silent half-load. Its own sync log (bt-sync-*.txt on
    /// the Desktop) can record "BootstrapAborted ... restartRequired=True" — meaning
    /// its deferred patches were NEVER applied and the whole session runs with broken
    /// sync (2026-08-19 20:46, client: stale RuntimeDataCache .rdc from a different
    /// version failed the action-cache audit; symptoms were missing partner armies,
    /// joins not registering on the host, speed desync). The mod does not surface
    /// this to the player, so we do: scan the recent tail of those logs every couple
    /// of minutes and warn loudly on screen.
    /// </summary>
    internal static class BootstrapWatch
    {
        private const string Component = "bootstrap-watch";
        private const string Tag = "[BOOTSTRAP-WATCH]";
        private const string Needle = "BootstrapAborted";

        private static int _lastCheckTick;
        private static bool _warned;
        private static bool _testRegistered;

        /// <summary>A log scanner has no engine member to resolve, so it is always "active"; the
        /// self-test pins the two scanners (full-file and tail) on a synthetic BT log so a silent
        /// parser regression cannot pass as "no abort happened" (added 2026-09-04).</summary>
        internal static void Apply()
        {
            try
            {
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                Diag.Report(Component, true, "");
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            string path = null;
            try
            {
                path = Path.Combine(Path.GetTempPath(), "bltguard-bootstrapwatch-selftest-" + Guid.NewGuid().ToString("N") + ".txt");
                string body = "[HARMONY] NativeActionCatalogReady source=application-tick actions=5167\r\n" +
                              "[HARMONY] " + Needle + " reason=action-cache-mismatch restartRequired=True\r\n" +
                              "[HARMONY] a later line\r\n";
                File.WriteAllText(path, body, new UTF8Encoding(false));
                long lineStart = body.IndexOf("[HARMONY] " + Needle, StringComparison.Ordinal); // FullFind reports the hit LINE's start
                long exact = body.IndexOf(Needle, StringComparison.Ordinal);                    // TailFind reports the exact byte index
                long full = FullFind(path, Needle);
                long tail = TailFind(path, Needle);
                long absentFull = FullFind(path, "NeverInThisFile");
                long absentTail = TailFind(path, "NeverInThisFile");
                bool pass = full == lineStart && tail == exact && absentFull == -1 && absentTail == -1;
                return SelfHealing.TestResult.Of(Component + ".contract", pass,
                    pass ? Needle + " located by both the full-file and the tail scan; an absent needle reports -1"
                         : "full=" + full + " (want " + lineStart + ") tail=" + tail + " (want " + exact + ") absent=" + absentFull + "/" + absentTail);
            }
            catch (Exception ex)
            {
                return SelfHealing.TestResult.Of(Component + ".contract", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (path != null && File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>Startup pass: if the PREVIOUS session aborted, clear the stale
        /// cache BEFORE the co-op mod's bootstrap audits it this session.</summary>
        internal static void CheckAtStartup()
        {
            Scan(60 * 24, startup: true);
        }

        internal static void Tick()
        {
            try
            {
                if (_warned)
                {
                    return;
                }
                int now = Environment.TickCount;
                if (_lastCheckTick != 0 && now - _lastCheckTick < 120000 && now >= _lastCheckTick)
                {
                    return;
                }
                _lastCheckTick = now;
                Scan(30, startup: false);
            }
            catch
            {
            }
        }

        private static void Scan(int maxAgeMinutes, bool startup)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                foreach (string name in new[] { "bt-sync-client.txt", "bt-sync-host.txt", "bt-sync-solo.txt" })
                {
                    string path = Path.Combine(desktop, name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    if ((DateTime.Now - File.GetLastWriteTime(path)).TotalMinutes > maxAgeMinutes)
                    {
                        continue;
                    }
                    // Startup must scan the WHOLE file — the previous session's abort
                    // can sit megabytes before the end (live test 2026-08-19 21:14:
                    // abort at ~50KB of a 12.7MB log, missed by the 256KB tail).
                    // Mid-session ticks only need the tail, where new lines land.
                    long abortOffset = startup ? FullFind(path, "BootstrapAborted") : TailFind(path, "BootstrapAborted");
                    if (abortOffset < 0)
                    {
                        continue;
                    }
                    if (abortOffset <= ReadHandledOffset(name))
                    {
                        continue; // this abort was already handled on a previous pass
                    }
                    WriteHandledOffset(name, abortOffset);
                    SelfHealing.RecordFire(Component); // feeds GUARD ACTIVITY (retirable once BT regenerates its cache itself)
                    int cleared = ClearStaleCache();
                    _warned = !startup;
                    Log.Info("[BOOTSTRAP-WATCH] co-op mod reported BootstrapAborted in " + name +
                             " — its patches were NOT fully applied. Auto-cleared " + cleared +
                             " cache file(s) (renamed to .stale). " + (startup ? "Cleared before this session's bootstrap." : "RESTART THE GAME to load cleanly."));
                    if (!startup)
                    {
                        Log.Screen("co-op mod did NOT fully load — cache auto-cleared, RESTART THE GAME");
                    }
                    return;
                }
            }
            catch
            {
            }
        }

        /// <summary>Rename (never delete) the co-op mod's RuntimeDataCache entries so
        /// its bootstrap rebuilds them fresh. The cache is regenerated data; renaming
        /// is reversible and is the remedy its own audit implies (restartRequired).</summary>
        private static int ClearStaleCache()
        {
            int cleared = 0;
            try
            {
                string binDir = Path.GetDirectoryName(typeof(BootstrapWatch).Assembly.Location);
                string modulesDir = Path.GetFullPath(Path.Combine(binDir, "..", "..", ".."));
                string cacheDir = Path.Combine(modulesDir, "BannerlordTogether", "RuntimeDataCache");
                if (!Directory.Exists(cacheDir))
                {
                    return 0;
                }
                foreach (string file in Directory.GetFiles(cacheDir, "*.rdc"))
                {
                    try
                    {
                        File.Move(file, file + ".stale-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                        cleared++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[BOOTSTRAP-WATCH] could not move " + Path.GetFileName(file) + ": " + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[BOOTSTRAP-WATCH] cache clear failed: " + ex.Message);
            }
            return cleared;
        }

        private static string StatePath
        {
            get
            {
                string binDir = Path.GetDirectoryName(typeof(BootstrapWatch).Assembly.Location);
                return Path.Combine(Path.GetFullPath(Path.Combine(binDir, "..", "..")), "bootstrapwatch.state");
            }
        }

        private static long ReadHandledOffset(string logName)
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    return -1;
                }
                foreach (string line in File.ReadAllLines(StatePath))
                {
                    int sep = line.IndexOf('|');
                    if (sep > 0 && line.Substring(0, sep) == logName)
                    {
                        long value;
                        if (long.TryParse(line.Substring(sep + 1), out value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch
            {
            }
            return -1;
        }

        private static void WriteHandledOffset(string logName, long offset)
        {
            try
            {
                System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
                if (File.Exists(StatePath))
                {
                    foreach (string line in File.ReadAllLines(StatePath))
                    {
                        if (!line.StartsWith(logName + "|", StringComparison.Ordinal))
                        {
                            lines.Add(line);
                        }
                    }
                }
                lines.Add(logName + "|" + offset);
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch
            {
            }
        }

        /// <summary>Chunked scan of the entire file for the LAST occurrence of needle;
        /// returns its approximate absolute offset or -1.</summary>
        private static long FullFind(string path, string needle)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    long lastHit = -1;
                    long consumed = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf(needle, StringComparison.Ordinal) >= 0)
                        {
                            lastHit = consumed;
                        }
                        consumed += line.Length + 2;
                    }
                    return lastHit;
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Returns the approximate absolute file offset of the LAST occurrence
        /// of needle within the file's tail, or -1 when absent.</summary>
        private static long TailFind(string path, string needle)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    const int tailSize = 262144;
                    long start = Math.Max(0, stream.Length - tailSize);
                    stream.Seek(start, SeekOrigin.Begin);
                    byte[] buffer = new byte[stream.Length - start];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int chunk = stream.Read(buffer, read, buffer.Length - read);
                        if (chunk <= 0)
                        {
                            break;
                        }
                        read += chunk;
                    }
                    int index = Encoding.UTF8.GetString(buffer, 0, read).LastIndexOf(needle, StringComparison.Ordinal);
                    return index < 0 ? -1 : start + index;
                }
            }
            catch
            {
                return -1;
            }
        }
    }
}
