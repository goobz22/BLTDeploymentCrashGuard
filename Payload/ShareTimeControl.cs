using System;
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
    ///
    /// Health component `share-time-control` (config-off reports healthy as "disabled by config";
    /// no BT reports healthy as "inert"); fire id the same, once per grant; self-test
    /// `share-time-control.contract` pins the two BT members and the grant decision (added
    /// 2026-09-04 — this enabler used to be invisible to MOD HEALTH).
    /// </summary>
    internal static class ShareTimeControl
    {
        private const string Component = "share-time-control";
        private const string Tag = "[SHARE-TIME]";

        private static bool? _enabled;
        private static bool _resolved;
        private static bool _grantedLogged;
        private static int _lastTick;
        private static bool _testRegistered;

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
                    _enabled = GuardConfig.Bool("shareTimeControl", true);
                }
                return _enabled.Value;
            }
        }

        /// <summary>Tick-driven; Apply registers health + the self-test and, when BannerlordTogether
        /// is already loaded, resolves its members now so the apply-time health line is accurate.
        /// When BT is not up yet, Tick re-resolves later and re-reports (health is keyed).</summary>
        internal static void Apply()
        {
            try
            {
                if (!_testRegistered)
                {
                    _testRegistered = true;
                    SelfHealing.RegisterTest(SelfTest);
                }
                if (!Enabled)
                {
                    Diag.Report(Component, true, "disabled by config");
                    return;
                }
                if (!PeerDetection.IsCoopAssemblyLoaded())
                {
                    Diag.Report(Component, true, "inert — BannerlordTogether not loaded");
                    return;
                }
                Resolve(); // reports
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " apply failed: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
            }
        }

        /// <summary>The whole decision, engine-free so the self-test can pin it: grant only while the
        /// feature is on, nothing has been granted this session, this process is the authority and
        /// BT does not already report the permission as on.</summary>
        internal static bool NeedsGrant(bool enabled, bool alreadyGranted, bool isHost, bool alreadyOn)
        {
            return enabled && !alreadyGranted && isHost && !alreadyOn;
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

                if (!Resolve())
                {
                    return;
                }
                bool isHost = IsHost();

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
                if (!NeedsGrant(Enabled, _grantedLogged, isHost, already))
                {
                    if (isHost && already && !_grantedLogged)
                    {
                        _grantedLogged = true;
                        Log.Info(Tag + " client time control is enabled — either player can pause/play/fast-forward");
                    }
                    return; // only the authority grants; the client process no-ops
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
                    SelfHealing.RecordFire(Component);
                    Log.Info(Tag + " granted client time control — either player can now pause/play/fast-forward");
                    Log.Screen("shared time control enabled — either player controls speed");
                }
                else if (reason != null && !reason.Contains("no longer connected") && !reason.Contains("No connected"))
                {
                    // benign "no client yet" reasons are silent; log anything else once-ish
                    Log.Info(Tag + " not granted yet (" + reason + ") — will retry");
                }
            }
            catch (Exception ex)
            {
                Log.Info(Tag + " tick error: " + ex.Message);
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

        private static MethodInfo ToggleMethod(Type coopSubModule)
        {
            return AccessTools.Method(coopSubModule, "ToggleClientTimeControlPermission",
                new[] { typeof(bool).MakeByRefType(), typeof(string).MakeByRefType() });
        }

        private static MethodInfo MenuCheckMethod(Type coopSubModule)
        {
            return AccessTools.Method(coopSubModule, "IsClientTimeControlEnabledForCurrentMenu");
        }

        private static bool Resolve()
        {
            if (_resolved)
            {
                return _coopSubModule != null && _toggle != null && _isEnabledForMenu != null;
            }
            try
            {
                _coopSubModule = PeerDetection.FindCoopType("CoopSubModule");
                _coopSession = PeerDetection.FindCoopType("CoopSession");
                if (_coopSubModule == null)
                {
                    // Latch only once BT is actually loaded: before that a miss means "not yet", not a rename.
                    if (PeerDetection.IsCoopAssemblyLoaded())
                    {
                        _resolved = true;
                        Log.Info(Tag + " CoopSubModule not found — shared time control INACTIVE (BannerlordTogether renamed it?)");
                        Diag.Report(Component, false, "CoopSubModule not found (BannerlordTogether renamed it?)");
                    }
                    return false;
                }
                _resolved = true;
                _toggle = ToggleMethod(_coopSubModule);
                _isEnabledForMenu = MenuCheckMethod(_coopSubModule);
                if (_coopSession != null)
                {
                    _isHostProp = AccessTools.Property(_coopSession, "IsHost");
                    _isHostField = AccessTools.Field(_coopSession, "IsHost");
                }
                if (_toggle == null || _isEnabledForMenu == null)
                {
                    Log.Info(Tag + " required method(s) not found (toggle=" + (_toggle != null) + " menuCheck=" + (_isEnabledForMenu != null) + ") — shared time control INACTIVE (mod version changed?)");
                    Diag.Report(Component, false, "CoopSubModule." + (_toggle == null ? "ToggleClientTimeControlPermission" : "IsClientTimeControlEnabledForCurrentMenu") + " not found (BannerlordTogether update?)");
                    return false;
                }
                Diag.Report(Component, true, "");
                Log.Info(Tag + " shared time control enabler active (grants client time control on the authority)");
                return true;
            }
            catch (Exception ex)
            {
                _resolved = true;
                Log.Info(Tag + " resolve error: " + ex.Message);
                Diag.Report(Component, false, ex.Message);
                return false;
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            bool btLoaded = PeerDetection.IsCoopAssemblyLoaded();
            Type coopSubModule = PeerDetection.FindCoopType("CoopSubModule");
            bool members = !btLoaded || (coopSubModule != null && ToggleMethod(coopSubModule) != null && MenuCheckMethod(coopSubModule) != null);
            bool decisions =
                NeedsGrant(true, false, true, false) &&
                !NeedsGrant(false, false, true, false) &&   // config off
                !NeedsGrant(true, true, true, false) &&     // already granted this session
                !NeedsGrant(true, false, false, false) &&   // client process never grants
                !NeedsGrant(true, false, true, true);       // BT already reports it on
            bool pass = members && decisions;
            return SelfHealing.TestResult.Of(Component + ".contract", pass,
                pass ? (btLoaded ? "CoopSubModule.ToggleClientTimeControlPermission + IsClientTimeControlEnabledForCurrentMenu and the grant decision verified"
                                 : "BannerlordTogether not loaded — grant decision verified only")
                     : "members=" + members + " decisions=" + decisions);
        }
    }
}
