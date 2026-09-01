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
        private const long MaxLogBytes = 8 * 1024 * 1024; // roll over past 8 MB

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
        /// Roll the log to CrashGuard.log.1 past the size cap. Re-checked every 512 writes
        /// (not once per launch — the old once-per-session latch let a single tracing-on
        /// session grow the file to 283 MB because the only check ran while it was small).
        /// Called under Sync.
        /// </summary>
        private static void RotateIfNeeded()
        {
            if (_writesSinceRotateCheck++ % 512 != 0)
            {
                return;
            }
            try
            {
                string path = LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    string backup = path + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                    File.Move(path, backup);
                }
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
