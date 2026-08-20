using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Auto-grants CLIENT time control so EITHER player can pause/play/fast-forward
    /// (guardconfig.json "shareTimeControl", default true).
    ///
    /// BannerlordTogether already supports shared time control but it defaults OFF:
    /// the client's time buttons route through CoopSubModule.TrySendClientTimeControlCommand,
    /// which bails at `if (!CoopSession.AllowClientTimeControl)` with the message
    /// "[BT] Client time controls are disabled by the host." (observed live 2026-08-19:
    /// client stuck at whatever speed the authority broadcasts, cannot pause/normal/FF).
    /// The host grants it via CoopSubModule.ToggleClientTimeControlPermission — the
    /// no-arg overload auto-targets the single gameplay client (exactly the 2-player
    /// case, host-or-dedicated). We invoke that on the authority process until the
    /// permission is on. Not a hack — it enables a shipped feature that is off by
    /// default.
    ///
    /// Runs only on the authority (IsHost); on the client process it no-ops. All access
    /// is by-name reflection so it survives if unrelated members change.
    /// </summary>
    internal static class ShareTimeControl
    {
        private static bool? _enabled;
        private static bool _resolved;
        private static bool _grantedLogged;
        private static int _lastTick;

        private static Type _coopSubModule;
        private static MethodInfo _toggle;              // ToggleClientTimeControlPermission(out bool, out string)
        private static MethodInfo _isEnabledForMenu;    // IsClientTimeControlEnabledForCurrentMenu()
        private static Type _coopSession;
        private static PropertyInfo _isHostProp;
        private static FieldInfo _isHostField;

        private static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    _enabled = ReadConfig();
                }
                return _enabled.Value;
            }
        }

        internal static void Tick()
        {
            try
            {
                // Once granted we stop — a later host toggle-off is theirs to keep,
                // and this prevents any OFF/ON churn from a misread state check.
                if (!Enabled || _grantedLogged)
                {
                    return;
                }
                int now = Environment.TickCount;
                if (_lastTick != 0 && now - _lastTick < 3000 && now >= _lastTick)
                {
                    return;
                }
                _lastTick = now;

                if (!Resolve() || !IsHost())
                {
                    return; // only the authority grants; client process no-ops
                }

                // Already on? nothing to do.
                bool already = false;
                try
                {
                    object v = _isEnabledForMenu != null ? _isEnabledForMenu.Invoke(null, null) : null;
                    already = v is bool && (bool)v;
                }
                catch
                {
                }
                if (already)
                {
                    if (!_grantedLogged)
                    {
                        _grantedLogged = true;
                        Log.Info("[SHARE-TIME] client time control is enabled — either player can pause/play/fast-forward");
                    }
                    return;
                }

                // Grant it. The no-arg toggle validates host + single gameplay client
                // itself and returns (enabled, reason). It's a toggle, so if it flips
                // OFF we call once more to force ON.
                bool enabled = InvokeToggle(out string reason);
                if (!enabled && reason == null)
                {
                    // toggled the wrong way (was already true and menu-check lied) — flip back
                    enabled = InvokeToggle(out reason);
                }
                if (enabled)
                {
                    _grantedLogged = true;
                    Log.Info("[SHARE-TIME] granted client time control — either player can now pause/play/fast-forward");
                    Log.Screen("shared time control enabled — either player controls speed");
                }
                else if (reason != null && !reason.Contains("no longer connected") && !reason.Contains("No connected"))
                {
                    // benign "no client yet" reasons are silent; log anything else once-ish
                    Log.Info("[SHARE-TIME] not granted yet (" + reason + ") — will retry");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[SHARE-TIME] tick error: " + ex.Message);
            }
        }

        private static bool InvokeToggle(out string reason)
        {
            reason = null;
            try
            {
                object[] args = new object[2];
                _toggle.Invoke(null, args); // trust the out-param, not the (possibly void) return
                reason = args[1] as string;
                return args[0] is bool && (bool)args[0];
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static bool IsHost()
        {
            try
            {
                object v = _isHostProp != null ? _isHostProp.GetValue(null)
                    : (_isHostField != null ? _isHostField.GetValue(null) : null);
                return v is bool && (bool)v;
            }
            catch
            {
                return false;
            }
        }

        private static bool Resolve()
        {
            if (_resolved)
            {
                return _coopSubModule != null && _toggle != null && _isEnabledForMenu != null;
            }
            _resolved = true;
            try
            {
                _coopSubModule = PeerDetection.FindCoopType("CoopSubModule");
                _coopSession = PeerDetection.FindCoopType("CoopSession");
                if (_coopSubModule == null)
                {
                    return false;
                }
                _toggle = AccessTools.Method(_coopSubModule, "ToggleClientTimeControlPermission",
                    new[] { typeof(bool).MakeByRefType(), typeof(string).MakeByRefType() });
                _isEnabledForMenu = AccessTools.Method(_coopSubModule, "IsClientTimeControlEnabledForCurrentMenu");
                if (_coopSession != null)
                {
                    _isHostProp = AccessTools.Property(_coopSession, "IsHost");
                    _isHostField = AccessTools.Field(_coopSession, "IsHost");
                }
                if (_toggle == null || _isEnabledForMenu == null)
                {
                    Log.Info("[SHARE-TIME] required method(s) not found (toggle=" + (_toggle != null) + " menuCheck=" + (_isEnabledForMenu != null) + ") — shared time control INACTIVE (mod version changed?)");
                    return false;
                }
                Log.Info("[SHARE-TIME] shared time control enabler active (grants client time control on the authority)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("[SHARE-TIME] resolve error: " + ex.Message);
                return false;
            }
        }

        private static bool ReadConfig()
        {
            try
            {
                string binDir = Path.GetDirectoryName(typeof(ShareTimeControl).Assembly.Location);
                string configPath = Path.Combine(Path.GetFullPath(Path.Combine(binDir, "..", "..")), "guardconfig.json");
                if (File.Exists(configPath))
                {
                    string text = File.ReadAllText(configPath);
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, "\"shareTimeControl\"\\s*:\\s*false"))
                    {
                        return false;
                    }
                }
            }
            catch
            {
            }
            return true; // default on
        }
    }
}
