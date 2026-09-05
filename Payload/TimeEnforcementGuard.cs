using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Records which of OUR prefixes vetoed the current Campaign.set_TimeControlMode write.
    /// Three of our prefixes sit on that setter: two bool-returning vetoers (TimeEnforcementGuard's
    /// solo neutralizer, MapClickSpeedKeeper) and the void log-only TimeTrace prefix. Harmony
    /// skips the remaining BOOL prefixes once one has returned false — so at most one of our two
    /// vetoers fires per write — but runs every VOID prefix regardless, so the tracer always sees
    /// the call yet cannot tell which vetoer won (audit 2026-09-04). A vetoing prefix calls
    /// Note(name) — only for set_TimeControlMode, the setter the tracer observes; the tracer's
    /// postfix calls Take() on EVERY observed write so a note never outlives the call that set it.
    /// A note older than StaleMs (possible only while tracing is off and nothing consumes) is
    /// discarded. [ThreadStatic] because the tracer must never mix two threads' vetoes.
    /// </summary>
    internal static class TimeVeto
    {
        private const int StaleMs = 200;

        [ThreadStatic]
        private static string _lastVetoedBy;
        [ThreadStatic]
        private static int _vetoTick;

        internal static void Note(string who)
        {
            _lastVetoedBy = who;
            _vetoTick = Environment.TickCount;
        }

        internal static string Take()
        {
            string who = _lastVetoedBy;
            int tick = _vetoTick;
            _lastVetoedBy = null;
            if (who == null)
            {
                return null;
            }
            int age = Environment.TickCount - tick;
            return age >= 0 && age <= StaleMs ? who : null;
        }
    }

    /// <summary>
    /// Fixes: after loading a save mid-session while hosting alone, fast-forward stops
    /// working until relaunch — the co-op mod's CoopCampaignBehavior.EnforcePlaySpeed
    /// runs every campaign tick and forces UnstoppablePlay, stomping the player's
    /// chosen speed (evidence: CrashGuard.log 2026-08-19 00:07-00:08).
    ///
    /// v2 approach — RUN BUT NEUTRALIZE: skipping the method entirely (v1) let the
    /// mod's internal time-state machine go stale, which plausibly contributed to a
    /// joining player meeting a stuck shared-pause (2026-08-19 00:32-00:35: host
    /// unpause clicks vetoed while a peer was connected, despite shared time control
    /// being the mod's design). Now the method always RUNS — its bookkeeping and any
    /// sync side effects stay fresh — but while NO remote player is connected its
    /// writes to Campaign.TimeControlMode / TimeControlModeLock are blocked, so solo
    /// time control stays with the player. With a peer connected nothing is touched.
    ///
    /// Also installs log-only tracers on CoopSubModule.SetPaused / ApplyTimeState
    /// (names from runtime stack traces) so shared-pause intent flow is visible in
    /// the log during co-op sessions.
    /// </summary>
    internal static class TimeEnforcementGuard
    {
        private static bool _applied;
        private static bool _pauseTraceApplied;
        private static int _lastCheckTick;
        private static bool _peersConnected;
        private static bool _skipLogged;

        [ThreadStatic]
        private static bool _inSoloEnforce;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }
            ApplyEnforceGuard(harmony);
            ApplyPauseTrace(harmony);
        }

        private static void ApplyEnforceGuard(Harmony harmony)
        {
            if (_applied)
            {
                return;
            }
            try
            {
                Type behavior = PeerDetection.FindCoopType("CoopCampaignBehavior");
                if (behavior == null)
                {
                    return; // co-op mod absent (or not loaded yet — Apply is retried later)
                }
                int count = 0;
                foreach (MethodInfo method in behavior.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "EnforcePlaySpeed" || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method,
                            new HarmonyMethod(typeof(TimeEnforcementGuard), nameof(EnforcePrefix)),
                            null, null,
                            new HarmonyMethod(typeof(TimeEnforcementGuard), nameof(EnforceFinalizer)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[TIME-GUARD] could not patch EnforcePlaySpeed: " + exOne.Message);
                    }
                }
                if (count > 0)
                {
                    // block only the time writes made DURING the enforcer while solo
                    foreach (string setterName in new[] { "set_TimeControlMode", "SetTimeControlModeLock", "set_TimeControlModeLock" })
                    {
                        Type campaign = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Campaign");
                        MethodInfo setter = campaign != null ? AccessTools.Method(campaign, setterName) : null;
                        if (setter != null)
                        {
                            harmony.Patch(setter, new HarmonyMethod(typeof(TimeEnforcementGuard), nameof(BlockSoloEnforceWritePrefix)));
                        }
                    }
                    _applied = true;
                    Log.Info("[TIME-GUARD] EnforcePlaySpeed neutralizer active (" + count + " method(s)) — runs every tick, writes blocked while no remote player is connected");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[TIME-GUARD] apply failed: " + ex.Message);
            }
        }

        private static void ApplyPauseTrace(Harmony harmony)
        {
            if (_pauseTraceApplied)
            {
                return;
            }
            try
            {
                Type coopSubModule = PeerDetection.FindCoopType("CoopSubModule");
                if (coopSubModule == null)
                {
                    return;
                }
                int count = 0;
                foreach (MethodInfo method in coopSubModule.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if ((method.Name != "SetPaused" && method.Name != "ApplyTimeState") || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(TimeEnforcementGuard), nameof(PauseTracePrefix)));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[TIME-GUARD] could not trace " + method.Name + ": " + exOne.Message);
                    }
                }
                if (count > 0)
                {
                    _pauseTraceApplied = true;
                    Log.Info("[TIME-GUARD] shared-pause tracer active on " + count + " method(s)");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[TIME-GUARD] pause-trace apply failed: " + ex.Message);
            }
        }

        // ---- hooks ----

        private static void EnforcePrefix()
        {
            try
            {
                int now = Environment.TickCount;
                if (_lastCheckTick == 0 || now - _lastCheckTick > 2000 || now < _lastCheckTick)
                {
                    _lastCheckTick = now;
                    // Fail toward co-op: neutralize only on a CONFIDENT "no session".
                    bool connected = PeerDetection.AnyRemotePeerConnected() != false;
                    if (connected != _peersConnected)
                    {
                        _skipLogged = false;
                        Log.Info("[TIME-GUARD] peer state -> " + (connected ? "CONNECTED (enforcement fully re-enabled)" : "alone (write-neutralizing)") + " | " + PeerDetection.Snapshot());
                    }
                    _peersConnected = connected;
                }
                if (_peersConnected)
                {
                    return; // real co-op: enforcement untouched
                }
                _inSoloEnforce = true; // method runs; its time writes are blocked below
                if (!_skipLogged)
                {
                    _skipLogged = true;
                    Log.Info("[TIME-GUARD] neutralizing EnforcePlaySpeed time-writes — no remote player connected (state machine keeps running; auto-restores when one joins)");
                }
            }
            catch
            {
            }
        }

        private static Exception EnforceFinalizer(Exception __exception)
        {
            _inSoloEnforce = false;
            return __exception;
        }

        private static bool BlockSoloEnforceWritePrefix(MethodBase __originalMethod)
        {
            if (_inSoloEnforce)
            {
                // Note the veto only for set_TimeControlMode — the one setter the [TIME] tracer
                // observes and therefore consumes the note on. A note from the two lock setters
                // would outlive its call and be misattributed to a later write (review 2026-09-04).
                if (__originalMethod != null && __originalMethod.Name == "set_TimeControlMode")
                {
                    TimeVeto.Note("TIME-GUARD");
                }
                return false;
            }
            return true;
        }

        private static bool CalledFromPacketHandler()
        {
            try
            {
                System.Diagnostics.StackFrame[] frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames == null)
                {
                    return false;
                }
                int examined = 0;
                foreach (System.Diagnostics.StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }
                    if (method.Name.IndexOf("Packet", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    if (++examined >= 12)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void PauseTracePrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                // Liveness stamp ONLY when the call arrives via network packet handling —
                // SetPaused/ApplyTimeState also fire once during solo game load
                // (2026-08-18 23:49 log: OnGameLoaded -> SetPaused -> ApplyTimeState),
                // which must not fake a connected session.
                if (CalledFromPacketHandler())
                {
                    PeerDetection.NoteCoopActivity();
                }
                StringBuilder sb = new StringBuilder("[TIME-GUARD] coop ");
                sb.Append(__originalMethod != null ? __originalMethod.Name : "?").Append('(');
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(__args[i] != null ? __args[i].ToString() : "null");
                    }
                }
                sb.Append(')');
                Log.Info(sb.ToString());
            }
            catch
            {
            }
        }
    }
}
