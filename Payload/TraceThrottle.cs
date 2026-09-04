using System;
using System.Collections.Generic;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Coalesces high-frequency, identical tracer lines into ONE full line + periodic
    /// rollups, so a per-tick tracer can no longer flood CrashGuard.log (the 2026-09-04
    /// incident: BannerlordTogether's EnforcePlaySpeed retries UnstoppablePlay every tick
    /// while our guard blocks the write, and the [TIME] tracer logged that blocked attempt
    /// — with a full stack — ~60x/second, filling the 8 MB log in minutes and rotating the
    /// real co-op-setup evidence off the end).
    ///
    /// Lives in the PAYLOAD (not the harness Log) on purpose: the harness DLL is locked
    /// while the game runs, so putting the throttle here lets the fix land via hot-reload
    /// with no restart. The first occurrence of a key logs in full; identical repeats are
    /// counted and flushed as "[repeat] key ×N in Ys (collapsed)" at most once per window.
    /// Statics are fresh per payload generation, so a reload starts clean automatically.
    /// </summary>
    internal static class TraceThrottle
    {
        private sealed class Run
        {
            public long Count;
            public int FirstTick;
            public int LastEmitTick;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Run> Runs = new Dictionary<string, Run>(StringComparer.Ordinal);
        private const int WindowMs = 5000;
        private const int MaxKeys = 512; // bound memory if keys are unexpectedly unique

        /// <summary>Log <paramref name="message"/> in full the first time this <paramref name="key"/>
        /// is seen; collapse identical repeats into a periodic count line. Ordering with plain
        /// Log.Info lines is best-effort (a run's tail count flushes on its next repeat or window,
        /// not instantly), which is exactly the tradeoff that stops the flood.</summary>
        internal static void Emit(string key, string message)
        {
            if (key == null)
            {
                key = "";
            }
            int now = Environment.TickCount;
            bool logFull = false;
            bool logRollup = false;
            long rollupCount = 0;
            float rollupSecs = 0f;
            lock (Sync)
            {
                Run run;
                if (!Runs.TryGetValue(key, out run))
                {
                    if (Runs.Count >= MaxKeys)
                    {
                        Runs.Clear(); // pathological key cardinality — reset rather than grow forever
                    }
                    Runs[key] = new Run { Count = 0, FirstTick = now, LastEmitTick = now };
                    logFull = true; // first occurrence -> full line (with its stack)
                }
                else
                {
                    run.Count++;
                    // negative delta guards against Environment.TickCount wrap (~24.9 days)
                    if (now - run.LastEmitTick >= WindowMs || now < run.LastEmitTick)
                    {
                        logRollup = true;
                        rollupCount = run.Count;
                        rollupSecs = (now - run.FirstTick) / 1000f;
                        run.Count = 0;
                        run.FirstTick = now;
                        run.LastEmitTick = now;
                    }
                }
            }
            if (logFull)
            {
                Log.Info(message);
            }
            else if (logRollup)
            {
                Log.Info("[repeat] " + key + " ×" + rollupCount + " in " + rollupSecs.ToString("0.0") + "s (identical, collapsed)");
            }
        }

        /// <summary>Drop all runs (e.g. between missions) so counts don't span unrelated states.</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                Runs.Clear();
            }
        }
    }
}
