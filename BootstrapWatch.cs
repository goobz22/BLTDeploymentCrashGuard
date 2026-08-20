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
        private static int _lastCheckTick;
        private static bool _warned;

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
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                foreach (string name in new[] { "bt-sync-client.txt", "bt-sync-host.txt", "bt-sync-solo.txt" })
                {
                    string path = Path.Combine(desktop, name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    if ((DateTime.Now - File.GetLastWriteTime(path)).TotalMinutes > 30)
                    {
                        continue; // stale log from an earlier session
                    }
                    if (TailContains(path, "BootstrapAborted"))
                    {
                        _warned = true;
                        Log.Info("[BOOTSTRAP-WATCH] the co-op mod reported BootstrapAborted in " + name + " — its patches are NOT fully applied and sync WILL be broken. Restart the game; if it repeats, remove Modules/BannerlordTogether/RuntimeDataCache/*.rdc and restart.");
                        Log.Screen("WARNING: co-op mod did NOT fully load (BootstrapAborted) — RESTART THE GAME");
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TailContains(string path, string needle)
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
                    return Encoding.UTF8.GetString(buffer, 0, read).IndexOf(needle, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
