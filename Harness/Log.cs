using System;
using System.IO;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BLTDeploymentCrashGuard
{
    /// <summary>Stable file logger in the harness (survives payload reloads). The role tag is
    /// SET by the payload (which owns peer detection) via SetRoleTag, so the harness has no
    /// dependency on payload types.</summary>
    public static class Log
    {
        private const long MaxLogBytes = 8 * 1024 * 1024; // roll a segment past 8 MB
        private const int MaxSegments = 6;                // keep CrashGuard.log.1 .. .6 (~48 MB of history)
        private const int RotateCheckEveryWrites = 256;   // amortise the FileInfo stat

        private static readonly object Sync = new object();
        private static string _path;
        private static string _roleTag = "?";
        private static int _writesSinceRotateCheck;

        public static string CurrentPath
        {
            get { return LogPath; }
        }

        public static string RoleTag
        {
            get { return _roleTag; }
        }

        /// <summary>Called by the payload's tick with the computed H/C/S role.</summary>
        public static void SetRoleTag(string tag)
        {
            if (!string.IsNullOrEmpty(tag))
            {
                _roleTag = tag;
            }
        }

        private static string LogPath
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        string binDir = Path.GetDirectoryName(typeof(Log).Assembly.Location);
                        string moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                        _path = Path.Combine(moduleRoot, "CrashGuard.log");
                    }
                    catch
                    {
                        _path = "BLTDeploymentCrashGuard.log";
                    }
                }
                return _path;
            }
        }

        public static void Info(string message)
        {
            try
            {
                lock (Sync)
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + _roleTag + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never take the game down
            }
        }

        /// <summary>
        /// Roll the log past the size cap, keeping a ROLLING WINDOW of segments
        /// (CrashGuard.log.1 = most recent full segment ... .MaxSegments = oldest) instead of
        /// a single overwrite. Rationale (2026-09-04 incident): a per-tick tracer could fill
        /// the 8 MB cap in minutes, and with only one backup the flip discarded the very
        /// evidence being chased. Several segments plus tracer coalescing keep a session's
        /// real events on disk. Re-checked every RotateCheckEveryWrites writes (not once per
        /// launch — the old once-per-session latch once let the file reach 283 MB because the
        /// only check ran while it was still small). Called under Sync.
        /// </summary>
        private static void RotateIfNeeded()
        {
            if (_writesSinceRotateCheck++ % RotateCheckEveryWrites != 0)
            {
                return;
            }
            try
            {
                string path = LogPath;
                if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogBytes)
                {
                    return;
                }
                // Drop the oldest, then shift each segment down one slot: .5 -> .6, ... .1 -> .2.
                string oldest = path + "." + MaxSegments;
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }
                for (int i = MaxSegments - 1; i >= 1; i--)
                {
                    string src = path + "." + i;
                    if (File.Exists(src))
                    {
                        File.Move(src, path + "." + (i + 1));
                    }
                }
                File.Move(path, path + ".1");
            }
            catch
            {
            }
        }

        public static void Screen(string message)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage("[Deploy Guard] " + message, new Color(1f, 0.75f, 0.3f)));
            }
            catch
            {
            }
        }
    }
}
