using System;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only tracer for the co-op SESSION ROLE across an in-game save load. The
    /// dedicated-authority role is set at launch from a command-line contract
    /// (--coop-authority -> CoopAuthorityRole.DedicatedGraphicalHost). Loading a save
    /// through the in-game menu appears to re-derive the role and drop dedicated mode
    /// ("switched to client mode out of dedicated server mode"). This captures the
    /// exact before/after role state so the fix can re-assert the correct role at the
    /// right point.
    ///
    /// Snapshots CoopSession.{IsHost,IsClient,IsDedicatedAuthority,AuthorityRole,
    /// HostMode,RequestedSessionRole,State,LocalGameplayPlayerCount,SharedSaveMode,
    /// AuthorityAutoLoadSaveName,IsOwnedAuthorityProcess} — bracketed around
    /// MBSaveLoad.LoadSaveGameData and again whenever it changes on tick.
    /// </summary>
    internal static class RoleTrace
    {
        private static readonly string[] Members =
        {
            "IsHost", "IsClient", "IsDedicatedAuthority", "AuthorityRole", "HostMode",
            "RequestedSessionRole", "State", "LocalGameplayPlayerCount", "SharedSaveMode",
            "AuthorityAutoLoadSaveName", "IsOwnedAuthorityProcess"
        };

        private static Type _coopSession;
        private static string _lastSnapshot = "";
        private static int _lastTick;
        private static bool _launchLogged;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _coopSession = PeerDetection.FindCoopType("CoopSession");
                if (_coopSession == null)
                {
                    return;
                }
                Type mbSaveLoad = AccessTools.TypeByName("TaleWorlds.Core.MBSaveLoad")
                    ?? AccessTools.TypeByName("TaleWorlds.SaveSystem.MBSaveLoad");
                int count = 0;
                if (mbSaveLoad != null)
                {
                    foreach (MethodInfo method in mbSaveLoad.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name != "LoadSaveGameData" || method.IsAbstract)
                        {
                            continue;
                        }
                        harmony.Patch(method,
                            new HarmonyMethod(typeof(RoleTrace), nameof(LoadPrefix)),
                            new HarmonyMethod(typeof(RoleTrace), nameof(LoadPostfix)));
                        count++;
                    }
                }
                Log.Info("[ROLE] role-transition tracer active (LoadSaveGameData hooks=" + count + ")");
            }
            catch (Exception ex)
            {
                Log.Info("[ROLE] apply failed: " + ex.Message);
            }
        }

        internal static void Tick()
        {
            try
            {
                if (_coopSession == null)
                {
                    return;
                }
                if (!_launchLogged)
                {
                    _launchLogged = true;
                    Log.Info("[ROLE] launch args coop-authority=" + LaunchedAsDedicated() + " | " + Snapshot());
                }
                int now = Environment.TickCount;
                if (_lastTick != 0 && now - _lastTick < 1000 && now >= _lastTick)
                {
                    return;
                }
                _lastTick = now;
                string snap = Snapshot();
                if (snap != _lastSnapshot)
                {
                    _lastSnapshot = snap;
                    Log.Info("[ROLE] changed -> " + snap);
                }
            }
            catch
            {
            }
        }

        private static void LoadPrefix(object[] __args)
        {
            string saveName = __args != null && __args.Length > 0 ? Convert.ToString(__args[0]) : "?";
            Log.Info("[ROLE] >>> LoadSaveGameData(" + saveName + ") BEFORE: " + Snapshot());
        }

        private static void LoadPostfix(object[] __args)
        {
            string saveName = __args != null && __args.Length > 0 ? Convert.ToString(__args[0]) : "?";
            Log.Info("[ROLE] <<< LoadSaveGameData(" + saveName + ") AFTER: " + Snapshot());
        }

        internal static bool LaunchedAsDedicated()
        {
            try
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, "--coop-authority", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--coop-dedicated-authority", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string Snapshot()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string member in Members)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(member).Append('=').Append(Read(member));
            }
            return sb.ToString();
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
