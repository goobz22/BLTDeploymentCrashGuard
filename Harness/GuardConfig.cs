using System;
using System.IO;
using System.Text.RegularExpressions;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Central reader for Modules/BLTDeploymentCrashGuard/guardconfig.json. Simple
    /// regex-based key lookup (no JSON dependency), cached per session. On first read
    /// it writes a fully-documented default file so every knob is discoverable.
    /// </summary>
    public static class GuardConfig
    {
        private static string _text;
        private static bool _loaded;

        public static string Path
        {
            get
            {
                string binDir = System.IO.Path.GetDirectoryName(typeof(GuardConfig).Assembly.Location);
                return System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(binDir, "..", "..")), "guardconfig.json");
            }
        }

        private static string Text
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try
                    {
                        if (!File.Exists(Path))
                        {
                            File.WriteAllText(Path, DefaultJson);
                        }
                        _text = File.ReadAllText(Path);
                    }
                    catch
                    {
                        _text = "";
                    }
                }
                return _text ?? "";
            }
        }

        public static bool Bool(string key, bool fallback)
        {
            try
            {
                Match m = Regex.Match(Text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
            return fallback;
        }

        public static string String(string key, string fallback)
        {
            try
            {
                Match m = Regex.Match(Text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
            catch
            {
            }
            return fallback;
        }

        private const string DefaultJson =
@"{
  ""_comment"": ""BLT Deployment Crash Guard settings. Delete this file to regenerate defaults."",

  ""safeMode"": false,          ""_safeMode"": ""true disables ALL guards/fixes/tracers (isolate whether this mod or BannerlordTogether is the cause)"",

  ""battleMode"": ""auto"",     ""_battleMode"": ""auto | solo | coop. auto = vanilla battles when hosting alone, co-op sync when a peer is connected"",
  ""timeAlwaysFlows"": true,    ""_timeAlwaysFlows"": ""true = campaign time does not auto-pause when your party idles"",
  ""shareTimeControl"": true,   ""_shareTimeControl"": ""host auto-grants the client time control so either player can pause/play/fast-forward"",

  ""noSickness"": true,         ""_noSickness"": ""true blocks the vanilla die-of-illness outcome for the local player's hero (each machine protects its own player). Stands down automatically if the third-party NoSickness mod is installed"",

  ""pregnancySync"": true,      ""_pregnancySync"": ""CO-OP: replicate host births to clients so both games share the same child (BT disables pregnancy for the client, host rolls run normally). Host-authoritative; self-disables outside a BT session"",

  ""stashSync"": true,          ""_stashSync"": ""CO-OP: keep settlement stashes identical on every machine — closing a stash screen broadcasts that stash's contents to the other players (same-clan players share one stash, like the workshop warehouse BT already syncs). Inert outside a BT session"",

  ""partyTroopsOnCreate"": true, ""_partyTroopsOnCreate"": ""when you create a new clan party, open the troop exchange with it immediately (vanilla creates the party with the leader only and expects you to find it on the map). Works solo and as co-op host; as a co-op client it waits for BannerlordTogether to confirm the party first"",

  ""coopOwnArmyCommand"": true, ""_coopOwnArmyCommand"": ""CO-OP: each player commands their OWN army in battle — the host's troops fight in formations I-IV (infantry/archers/cavalry/horse archers) and the client's in V-VIII, so BannerlordTogether approves each block for its owner (it only lets a player command a formation made purely of that player's troops; vanilla mixes both armies by class). Player heroes are never moved. Inert outside a live co-op battle"",

  ""siegeCommandAll"": true,    ""_siegeCommandAll"":""SIEGE DEFENSE: you command every formation and the ones you place hold where you put them. Vanilla's siege default hands formations to the AI, whose castle-defence tactic marches them to the walls and re-shuffles their troops (and abandons your spot on a breach). Also: defending a settlement your clan owns makes you the general even inside another lord's army. F6 still delegates a formation to the AI on purpose. Solo + co-op host; a co-op client follows the host's assignment"",

  ""myHero"": """",              ""_myHero"": ""SHARED-SAVE co-op: this machine's hero, by name (case-insensitive). When you load a shared save that was last played as the OTHER player's hero, the game switches you back to this hero. Only needed once per existing campaign — new campaigns and later sessions are recorded automatically (hero-identity.json)"",

  ""tracing"": false,           ""_tracing"": ""true enables verbose diagnostic tracers (mission/menu/control/time/coop-battle/role). Off for normal play; on for troubleshooting"",
  ""selfTest"": false,          ""_selfTest"": ""true runs each guard's decision-logic self-test at startup and logs PASS/FAIL (proves the fix wiring survived a BT update)"",

  ""logStreamBin"": """",        ""_logStreamBin"": ""a filebin.net bin id; when set, the log auto-uploads to filebin.net/<bin> every ~60s for remote debugging"",

  ""hotReload"": false,          ""_hotReload"": ""DEV ONLY. true + a .hotreload-dev marker file in the module root enables no-restart reload of the payload. Never for players (runtime code load is a code-injection surface)"",
  ""hotReloadRoslyn"": false,    ""_hotReloadRoslyn"": ""DEV ONLY. true watches payload .cs SOURCE and recompiles via Roslyn on save (requires the harness built with -p:Roslyn=true). false watches the prebuilt BLTDeploymentCrashGuard.Payload.dll (build-and-drop)"",
  ""payloadSourceDir"": """",     ""_payloadSourceDir"": ""DEV ONLY. path to the payload .cs source for Roslyn reload; defaults to a PayloadSource folder in the module root""
}
";
    }
}
