using System;
using System.IO;
using System.Net;
using System.Threading;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Zero-touch log streaming. When a bin id is configured (logstream.txt in the
    /// module folder, or "logStreamBin" in guardconfig.json), the mod uploads its
    /// CrashGuard.log to https://filebin.net/&lt;bin&gt; roughly once a minute whenever the
    /// log has grown. Both players configure the same bin once (the installer does it
    /// via the BLTGUARD_BIN environment variable) and from then on either side's
    /// diagnostics can be fetched at any time with no player action.
    /// Upload runs on a worker thread, fully try-caught; failures are logged and
    /// never affect the game.
    /// </summary>
    internal static class LogStreamer
    {
        private static bool _loaded;
        private static string _bin;
        private static int _lastUploadTick;
        private static long _lastLength;
        private static bool _announced;
        private static volatile bool _uploading;

        private static string BinId
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _bin = ReadBin();
                    if (_bin != null)
                    {
                        Log.Info("[STREAM] log streaming enabled -> https://filebin.net/" + _bin);
                    }
                }
                return _bin;
            }
        }

        private static string ReadBin()
        {
            try
            {
                string binDir = Path.GetDirectoryName(typeof(LogStreamer).Assembly.Location);
                string moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                string streamFile = Path.Combine(moduleRoot, "logstream.txt");
                if (File.Exists(streamFile))
                {
                    string id = File.ReadAllText(streamFile).Trim();
                    if (IsValidBin(id))
                    {
                        return id;
                    }
                }
                string configPath = Path.Combine(moduleRoot, "guardconfig.json");
                if (File.Exists(configPath))
                {
                    System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                        File.ReadAllText(configPath), "\"logStreamBin\"\\s*:\\s*\"([A-Za-z0-9_-]{4,63})\"");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsValidBin(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 4 || id.Length > 63)
            {
                return false;
            }
            foreach (char c in id)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                {
                    return false;
                }
            }
            return true;
        }

        internal static void Tick()
        {
            try
            {
                if (BinId == null || _uploading)
                {
                    return;
                }
                int now = Environment.TickCount;
                if (_lastUploadTick != 0 && now - _lastUploadTick < 60000 && now >= _lastUploadTick)
                {
                    return;
                }
                _lastUploadTick = now;
                string logPath = Log.CurrentPath;
                if (logPath == null || !File.Exists(logPath))
                {
                    return;
                }
                long length = new FileInfo(logPath).Length;
                if (length == _lastLength)
                {
                    return;
                }
                _lastLength = length;
                _uploading = true;
                ThreadPool.QueueUserWorkItem(delegate { Upload(logPath); });
            }
            catch
            {
                _uploading = false;
            }
        }

        private static void Upload(string logPath)
        {
            try
            {
                // Upload only the last 2MB — recent diagnostics live at the end, and a
                // full multi-MB log blew the request timeout (live test 21:16:08).
                const int maxUpload = 2 * 1024 * 1024;
                byte[] data;
                using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - maxUpload);
                    stream.Seek(start, SeekOrigin.Begin);
                    data = new byte[stream.Length - start];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int chunk = stream.Read(data, read, data.Length - read);
                        if (chunk <= 0)
                        {
                            break;
                        }
                        read += chunk;
                    }
                }
                string fileName = "blt-" + Log.RoleTag + "-" + Sanitize(Environment.MachineName) + ".log";
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://filebin.net");
                request.Method = "POST";
                request.Headers["bin"] = BinId;
                request.Headers["filename"] = fileName;
                request.ContentType = "application/octet-stream";
                request.ContentLength = data.Length;
                request.Timeout = 120000;
                request.ReadWriteTimeout = 120000;
                using (Stream body = request.GetRequestStream())
                {
                    body.Write(data, 0, data.Length);
                }
                using (request.GetResponse())
                {
                }
                if (!_announced)
                {
                    _announced = true;
                    Log.Info("[STREAM] first upload done: https://filebin.net/" + BinId + "/" + fileName + " (~every 60s while the log grows)");
                    Log.Screen("log streaming active");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[STREAM] upload failed: " + ex.Message);
            }
            finally
            {
                _uploading = false;
            }
        }

        private static string Sanitize(string value)
        {
            char[] buffer = value.ToCharArray();
            for (int i = 0; i < buffer.Length; i++)
            {
                if (!char.IsLetterOrDigit(buffer[i]))
                {
                    buffer[i] = '_';
                }
            }
            return new string(buffer);
        }
    }
}
