using System;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// ROOT FIX for "I can't unpause after someone joined" (field log 2026-08-22 23:43-23:49).
    /// Decompile-proven chain in BannerlordTogether:
    ///
    ///  - Campaign pause is a SET of named reasons (CoopSubModule._pauseCoordinator); the game is
    ///    paused while ANY reason is active. A joining player's save transfer holds the "SaveSync"
    ///    reason (and "HeroCreation" while they build their hero).
    ///  - The host's pause key only toggles the MANUAL reason (ToggleHostManualPause), so it can
    ///    never clear a join hold — and because the paused state doesn't change, BT's
    ///    "showMessage on state change" check shows NOTHING. The press is silently swallowed.
    ///  - The "host keeps playing while the client loads" fast-join path is gated (CK.A) to:
    ///    session Ready AND not a spectator AND the joiner already has a character AND at least
    ///    one OTHER gameplay peer. A spectator or first-time joiner into a solo-hosted game fails
    ///    that, so the legacy path hard-holds the host for the joiner's ENTIRE download + load +
    ///    hero creation — and a joiner stuck in a retry loop holds the host frozen FOREVER.
    ///
    /// BT itself treats cancelling a stuck transfer as a sanctioned recovery: its own watchdog
    /// calls the transfer-cancel router (resets the transfer, clears both pause reasons, tells the
    /// joiner to reconnect, "existing players can continue"), and it ships a host-facing
    /// "Skip Resync Wait" button for a related wait. This fix gives the host's own pause key that
    /// same authority, with consent:
    ///
    ///   press 1 while a join hold is swallowing your unpause -> on-screen explanation of WHO is
    ///     holding time (fixes the silent swallow), and a cancel window is armed;
    ///   press again within the window -> invoke BT's own transfer-cancel router (the exact method
    ///     its player-state timeout uses), then clear the manual pause reason so time resumes.
    ///
    /// Self-disabling: it only ever acts when a SaveSync/HeroCreation reason is actively holding
    /// the pause AND the player presses a time key — if BT unblocks legacy joins upstream, this
    /// never fires (visible as never-fired in the health report).
    /// </summary>
    internal static class JoinSyncPauseEscape
    {
        private const int ArmWindowMs = 6000;

        private static bool _applied;
        private static int _armedAtTick;

        // resolved reflection targets (BT v0.5.0.1; health-reported when they drift)
        private static MethodInfo _mapPauseReason;      // CoopSubModule.MapPauseReason(string) -> reason enum
        private static FieldInfo _pauseCoordinatorField; // CoopSubModule._pauseCoordinator (read live per query — survives a reassigned coordinator)
        private static MethodInfo _reasonActiveQuery;   // coordinator.IsActive(reason) -> bool (found by signature)
        private static MethodInfo _setPaused;           // CoopSubModule.SetPaused(bool, string, bool, string)
        private static MethodInfo _cancelTransfer;      // transfer coordinator "A"(string reason, string message, bool notify)
        private static object _reasonSaveSync;          // boxed enum value for "SaveSync"
        private static object _reasonHeroCreation;      // boxed enum value for "HeroCreation"

        internal enum EscapeAction
        {
            None,
            Arm,
            Cancel
        }

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                Type coopSubModule = PeerDetection.FindCoopType("CoopSubModule");
                if (coopSubModule == null)
                {
                    // Co-op mod absent or not loaded yet — Apply is retried later. Report either way so
                    // the health line never silently omits this component; a later retry replaces it.
                    bool btLoaded = PeerDetection.IsCoopAssemblyLoaded();
                    Diag.Report("join-sync-pause-escape", !btLoaded, btLoaded ? "CoopSubModule not found (BannerlordTogether renamed it?)" : "inert — BannerlordTogether not loaded");
                    return;
                }

                MethodInfo toggle = FindDeclared(coopSubModule, "ToggleHostManualPause");
                MethodInfo normalSpeed = FindDeclared(coopSubModule, "ApplyHostNormalSpeed");
                _mapPauseReason = FindDeclared(coopSubModule, "MapPauseReason");
                _setPaused = FindDeclared(coopSubModule, "SetPaused");
                _pauseCoordinatorField = coopSubModule.GetField("_pauseCoordinator", BindingFlags.NonPublic | BindingFlags.Static);
                object coordinator = _pauseCoordinatorField != null ? _pauseCoordinatorField.GetValue(null) : null;

                string missing = "";
                if (toggle == null) { missing += " ToggleHostManualPause"; }
                else if (toggle.ReturnType != typeof(bool)) { missing += " ToggleHostManualPause(bool-return)"; }
                if (_mapPauseReason == null) { missing += " MapPauseReason"; }
                if (_setPaused == null) { missing += " SetPaused"; }
                if (coordinator == null) { missing += " _pauseCoordinator"; }
                if (missing.Length > 0)
                {
                    Log.Info("[JOIN-ESCAPE] inactive — CoopSubModule members not found:" + missing + " (BT update?)");
                    Diag.Report("join-sync-pause-escape", false, "missing:" + missing.Trim());
                    return;
                }

                _reasonSaveSync = _mapPauseReason.Invoke(null, new object[] { "SaveSync" });
                _reasonHeroCreation = _mapPauseReason.Invoke(null, new object[] { "HeroCreation" });
                _reasonActiveQuery = FindReasonQuery(coordinator.GetType(), _mapPauseReason.ReturnType);
                _cancelTransfer = FindTransferCancel();

                if (_reasonActiveQuery == null || _cancelTransfer == null || _reasonSaveSync == null || _reasonHeroCreation == null)
                {
                    string detail = "reasonQuery=" + (_reasonActiveQuery != null) + " cancel=" + (_cancelTransfer != null)
                        + " saveSync=" + (_reasonSaveSync != null) + " heroCreation=" + (_reasonHeroCreation != null);
                    Log.Info("[JOIN-ESCAPE] inactive — could not resolve the pause query or transfer cancel (" + detail + ") (BT update?)");
                    Diag.Report("join-sync-pause-escape", false, detail);
                    return;
                }

                harmony.Patch(toggle, null, new HarmonyMethod(typeof(JoinSyncPauseEscape), nameof(TogglePostfix)));
                if (normalSpeed != null)
                {
                    harmony.Patch(normalSpeed, null, new HarmonyMethod(typeof(JoinSyncPauseEscape), nameof(NormalSpeedPostfix)));
                }
                _applied = true;
                Log.Info("[JOIN-ESCAPE] join-hold pause escape active — a swallowed unpause explains itself; pressing again cancels the stuck join via BT's own cancel");
                Diag.Report("join-sync-pause-escape", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[JOIN-ESCAPE] apply failed: " + ex.Message);
                Diag.Report("join-sync-pause-escape", false, ex.Message);
            }
        }

        // ---- resolution helpers ------------------------------------------------------------

        private static MethodInfo FindDeclared(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name == name)
                {
                    return method;
                }
            }
            return null;
        }

        /// <summary>The coordinator's "is this reason active" query — found by signature
        /// (1 parameter of the reason enum type, returns bool), not by obfuscated name.</summary>
        private static MethodInfo FindReasonQuery(Type coordinatorType, Type reasonType)
        {
            foreach (MethodInfo method in coordinatorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(bool))
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == reasonType)
                {
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// The save-transfer coordinator's cancel router: static void "A"(string reason,
        /// string message, bool notifyTarget) — the method BT's own player-state timeout calls.
        /// The declaring type is obfuscated, so it is found by fingerprint: the one BT type that
        /// both handles SaveTransferAckPacket and declares that cancel signature.
        /// </summary>
        private static MethodInfo FindTransferCancel()
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "BannerlordTogether")
                    {
                        continue;
                    }
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException loadEx)
                    {
                        types = loadEx.Types;
                    }
                    foreach (Type type in types)
                    {
                        if (type == null)
                        {
                            continue; // ReflectionTypeLoadException.Types null-pads unloadable entries — skip, keep scanning
                        }
                        MethodInfo cancel = null;
                        bool handlesAck = false;
                        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            ParameterInfo[] parameters = method.GetParameters();
                            foreach (ParameterInfo parameter in parameters)
                            {
                                if (parameter.ParameterType.Name == "SaveTransferAckPacket")
                                {
                                    handlesAck = true;
                                    break;
                                }
                            }
                            if (method.IsStatic && method.Name == "A" && method.ReturnType == typeof(void)
                                && parameters.Length == 3
                                && parameters[0].ParameterType == typeof(string)
                                && parameters[1].ParameterType == typeof(string)
                                && parameters[2].ParameterType == typeof(bool))
                            {
                                cancel = method;
                            }
                        }
                        if (handlesAck && cancel != null)
                        {
                            Log.Info("[JOIN-ESCAPE] transfer coordinator resolved: " + type.Name + "." + cancel.Name + "(string,string,bool)");
                            return cancel;
                        }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Info("[JOIN-ESCAPE] transfer-cancel lookup failed: " + ex.Message);
            }
            return null;
        }

        // ---- hooks -------------------------------------------------------------------------

        private static void TogglePostfix(bool __result)
        {
            HandleTimePress(__result);
        }

        private static void NormalSpeedPostfix()
        {
            HandleTimePress(true);
        }

        private static void HandleTimePress(bool handled)
        {
            try
            {
                int now = Environment.TickCount;
                bool armed = _armedAtTick != 0 && now - _armedAtTick >= 0 && now - _armedAtTick <= ArmWindowMs;
                string held = HeldJoinReasons();
                switch (Decide(handled, PeerDetection.ReadCoopStaticBool("IsPaused") == true, held != null, armed))
                {
                    case EscapeAction.Arm:
                        _armedAtTick = now;
                        Log.Screen("time is held by a joining player's sync (" + held + ") — press pause again within " + (ArmWindowMs / 1000) + "s to cancel their join");
                        Log.Info("[JOIN-ESCAPE] unpause swallowed by join hold (" + held + ") — cancel window armed");
                        break;
                    case EscapeAction.Cancel:
                        _armedAtTick = 0;
                        CancelJoinSync(held);
                        break;
                    default:
                        _armedAtTick = 0;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Info("[JOIN-ESCAPE] press handling error: " + ex.Message);
            }
        }

        /// <summary>Pure decision logic (self-tested). Only a press the co-op mod actually
        /// handled, that left the game paused, while a join reason holds the pause, acts.</summary>
        internal static EscapeAction Decide(bool pressHandled, bool stillPaused, bool joinHoldActive, bool cancelArmed)
        {
            if (!pressHandled || !stillPaused || !joinHoldActive)
            {
                return EscapeAction.None;
            }
            return cancelArmed ? EscapeAction.Cancel : EscapeAction.Arm;
        }

        /// <summary>"SaveSync", "HeroCreation", "SaveSync+HeroCreation", or null when neither
        /// holds the pause (or state is unreadable — never offer a cancel on uncertainty).</summary>
        private static string HeldJoinReasons()
        {
            try
            {
                object coordinator = _pauseCoordinatorField.GetValue(null);
                if (coordinator == null)
                {
                    return null;
                }
                bool saveSync = (bool)_reasonActiveQuery.Invoke(coordinator, new[] { _reasonSaveSync });
                bool heroCreation = (bool)_reasonActiveQuery.Invoke(coordinator, new[] { _reasonHeroCreation });
                if (saveSync && heroCreation)
                {
                    return "SaveSync+HeroCreation";
                }
                if (saveSync)
                {
                    return "SaveSync";
                }
                if (heroCreation)
                {
                    return "HeroCreation";
                }
            }
            catch (Exception ex)
            {
                Log.Info("[JOIN-ESCAPE] reason query error: " + ex.Message);
            }
            return null;
        }

        private static void CancelJoinSync(string held)
        {
            try
            {
                _cancelTransfer.Invoke(null, new object[]
                {
                    "host-cancelled",
                    "The host cancelled the join sync to keep playing. Reconnect to join again.",
                    true
                });
                // Our own presses may have toggled the manual pause reason on; clear it so the
                // cancel actually resumes time instead of leaving a manual pause behind.
                _setPaused.Invoke(null, new object[] { false, "Host", true, "join-escape" });
                SelfHealing.RecordFire("join-sync-pause-escape");
                Log.Screen("join sync cancelled — time is yours again (the joining player can reconnect)");
                Log.Info("[JOIN-ESCAPE] cancelled a stuck join hold (" + held + ") via BT's transfer-cancel router; manual pause reason cleared");
            }
            catch (Exception ex)
            {
                Log.Screen("could not cancel the join sync — see CrashGuard.log");
                Log.Info("[JOIN-ESCAPE] cancel failed: " + ex);
            }
        }

        // ---- self-test ----------------------------------------------------------------------

        private static SelfHealing.TestResult SelfTest()
        {
            bool targets = _reasonActiveQuery != null && _cancelTransfer != null && _setPaused != null
                && _reasonSaveSync != null && _reasonHeroCreation != null;
            // The reason query must be invocable (a pure read) without throwing.
            bool queryReads;
            try
            {
                _reasonActiveQuery.Invoke(_pauseCoordinatorField.GetValue(null), new[] { _reasonSaveSync });
                queryReads = true;
            }
            catch
            {
                queryReads = false;
            }
            bool logic =
                Decide(false, true, true, true) == EscapeAction.None      // press not handled -> never act
                && Decide(true, false, true, true) == EscapeAction.None   // game unpaused fine -> never act
                && Decide(true, true, false, true) == EscapeAction.None   // no join hold -> never act
                && Decide(true, true, true, false) == EscapeAction.Arm    // first swallowed press explains + arms
                && Decide(true, true, true, true) == EscapeAction.Cancel; // second press cancels
            bool pass = targets && queryReads && logic;
            return SelfHealing.TestResult.Of("join-sync-pause-escape.contract", pass,
                pass ? "targets resolved; reason query reads; arm/cancel decision logic correct"
                     : "targets=" + targets + " queryReads=" + queryReads + " logic=" + logic + " (BT update?)");
        }
    }
}
