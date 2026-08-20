using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only tracer for co-op BATTLE FORMATION — the encounter-send / battle-lease /
    /// live-battle-start decision points. Purpose: on a dedicated server with two
    /// gameplay clients, prove whether the authority forms ONE shared battle (both
    /// client parties on one side) or TWO independent per-client battles (each client
    /// vs the attacker, enemy parties double-counted). All hooks are void prefixes that
    /// append a [COOP-BATTLE] line with the co-op topology flags, so the streamed,
    /// role-tagged, timestamped logs from server + both clients line up.
    ///
    /// Hooks (BannerlordTogether internals, by-name reflection):
    ///  - BattleSyncBehavior.SendEncounterRequest        → authority sends a per-ghost encounter
    ///  - BattleSyncBehavior.ApplyClientStartedBattleLeaseState → mission-authority lease grants
    ///  - SpNativeBattleBehavior.StartLiveBattle         → a shared co-op battle actually starts
    ///  - SpNativeBattleBehavior.AttackLiveConsequence   → the "Attack (SP Co-op Battle)" attempt
    /// </summary>
    internal static class CoopBattleTrace
    {
        private static bool _applied;
        private static Type _coopSession;

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
            {
                return;
            }
            try
            {
                _coopSession = PeerDetection.FindCoopType("CoopSession");
                int n = 0;
                n += Hook(harmony, "BattleSyncBehavior", "SendEncounterRequest", nameof(SendEncounterPrefix));
                n += Hook(harmony, "BattleSyncBehavior", "ApplyClientStartedBattleLeaseState", nameof(LeasePrefix));
                n += Hook(harmony, "SpNativeBattleBehavior", "StartLiveBattle", nameof(StartLiveBattlePrefix));
                n += Hook(harmony, "SpNativeBattleBehavior", "AttackLiveConsequence", nameof(AttackLivePrefix));
                if (n > 0)
                {
                    _applied = true;
                    Log.Info("[COOP-BATTLE] battle-formation tracer active on " + n + " method(s)");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[COOP-BATTLE] apply failed: " + ex.Message);
            }
        }

        private static int Hook(Harmony harmony, string typeName, string methodName, string prefixName)
        {
            int count = 0;
            try
            {
                Type type = PeerDetection.FindCoopType(typeName);
                if (type == null)
                {
                    Log.Info("[COOP-BATTLE] type not found: " + typeName);
                    return 0;
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, new HarmonyMethod(typeof(CoopBattleTrace), prefixName));
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[COOP-BATTLE] could not hook " + typeName + "." + methodName + ": " + exOne.Message);
                    }
                }
                if (count == 0)
                {
                    Log.Info("[COOP-BATTLE] no method " + typeName + "." + methodName);
                }
            }
            catch (Exception ex)
            {
                Log.Info("[COOP-BATTLE] hook error for " + typeName + "." + methodName + ": " + ex.Message);
            }
            return count;
        }

        // ---- hooks ----

        private static void SendEncounterPrefix(object[] __args)
        {
            string attacker = Arg(__args, 0);
            string defenderGhost = Arg(__args, 1);
            Log.Info("[COOP-BATTLE] authority SendEncounterRequest attacker=" + attacker + " -> defenderGhost=" + defenderGhost + Topo());
        }

        private static void LeasePrefix(object[] __args)
        {
            string sessionId = Arg(__args, 0);
            string authKey = Arg(__args, 1);
            string leased = "?";
            if (__args != null && __args.Length > 2)
            {
                IEnumerable ids = __args[2] as IEnumerable;
                if (ids != null)
                {
                    StringBuilder sb = new StringBuilder("[");
                    bool first = true;
                    foreach (object id in ids)
                    {
                        if (!first) sb.Append(',');
                        sb.Append(id);
                        first = false;
                    }
                    leased = sb.Append(']').ToString();
                }
            }
            string active = __args != null && __args.Length > 3 ? Convert.ToString(__args[3]) : "?";
            Log.Info("[COOP-BATTLE] battle LEASE session=" + sessionId + " authKey=" + authKey + " leasedParties=" + leased + " active=" + active + Topo());
        }

        private static void StartLiveBattlePrefix()
        {
            Log.Info("[COOP-BATTLE] >>> StartLiveBattle (a shared co-op battle is starting)" + Topo());
        }

        private static void AttackLivePrefix()
        {
            Log.Info("[COOP-BATTLE] AttackLiveConsequence — 'Attack (SP Co-op Battle)' clicked" + Topo());
        }

        // ---- helpers ----

        private static string Arg(object[] args, int i)
        {
            if (args == null || i >= args.Length || args[i] == null)
            {
                return "(null)";
            }
            return args[i].ToString();
        }

        /// <summary>Co-op topology snapshot appended to every line, so server + client
        /// logs correlate: role, dedicated-authority, gameplay player count, in-battle.</summary>
        private static string Topo()
        {
            try
            {
                if (_coopSession == null)
                {
                    return "";
                }
                string host = Read("IsHost");
                string client = Read("IsClient");
                string dedicated = Read("IsDedicatedAuthority");
                string players = Read("LocalGameplayPlayerCount");
                string inBattle = Read("InSpNativeBattle");
                string battleId = Read("SpNativeBattleId");
                return " | host=" + host + " client=" + client + " dedicated=" + dedicated +
                       " localPlayers=" + players + " inSpBattle=" + inBattle + " battleId=" + battleId;
            }
            catch
            {
                return "";
            }
        }

        private static string Read(string member)
        {
            try
            {
                PropertyInfo p = _coopSession.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null)
                {
                    return Convert.ToString(p.GetValue(null)) ?? "null";
                }
                FieldInfo f = _coopSession.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null)
                {
                    return Convert.ToString(f.GetValue(null)) ?? "null";
                }
            }
            catch
            {
            }
            return "?";
        }
    }
}
