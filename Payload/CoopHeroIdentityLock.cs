using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Lets a co-op couple PASS HOSTING BACK AND FORTH on one shared save. The problem
    /// (field report 2026-08-30): a Bannerlord save stores exactly ONE player identity —
    /// whoever was MainHero when it was saved — so when the other person loads the shared
    /// save to host, they become the previous host's hero. BannerlordTogether's identity
    /// registry (slots, steam/password claims) is only consulted on the CLIENT join flow;
    /// nothing fixes the identity of the person LOADING the save (verified by assembly
    /// scan: SharedSaveMode is a bare session flag).
    ///
    /// Fix: a PER-MACHINE hero-identity map (hero-identity.json next to guardconfig.json)
    /// keyed by Campaign.UniqueGameId. On campaign load (host or solo — never as a BT
    /// client, whose hero BT assigns), if this machine's recorded hero for the campaign is
    /// not MainHero, the player is switched to their own hero through vanilla's
    /// ChangePlayerCharacterAction (the same mechanism death-succession uses).
    ///
    /// How a machine learns which hero is "mine" (deliberately explicit — a wrong guess
    /// would replicate the very bug this fixes):
    ///  - a NEW campaign records MainHero automatically (unambiguous: you created it);
    ///  - an EXISTING campaign is claimed once via guardconfig.json  "myHero": "Name"
    ///    (hero name, case-insensitive; player-clan heroes matched first) — after the
    ///    first claim the map maintains itself;
    ///  - on death-succession (recorded hero dead, you play on as the heir) the record
    ///    follows to the new MainHero.
    /// </summary>
    internal static class CoopHeroIdentityLock
    {
        private static bool _pendingClaim;
        private static bool _claimedThisCampaign;
        private static bool _guidanceLogged;
        private static int _lastMaintainTick;

        private static string MapPath
        {
            get { return Path.Combine(Path.GetDirectoryName(GuardConfig.Path) ?? "", "hero-identity.json"); }
        }

        internal static void Apply()
        {
            try
            {
                SelfHealing.RegisterTest(SelfTest);
                Diag.Report("hero-identity-lock", true, "");
                Log.Info("[IDENTITY] hero-identity lock active — loading a shared save always resumes THIS machine's hero (map: hero-identity.json)");
            }
            catch (Exception ex)
            {
                Diag.Report("hero-identity-lock", false, ex.Message);
            }
        }

        internal static void OnGameStart()
        {
            _pendingClaim = true;
            _claimedThisCampaign = false;
            _guidanceLogged = false;
        }

        internal static void Tick()
        {
            try
            {
                if (_pendingClaim)
                {
                    if (Campaign.Current == null || Hero.MainHero == null)
                    {
                        return; // campaign still coming up — try again next tick
                    }
                    if (TaleWorlds.MountAndBlade.Mission.Current != null)
                    {
                        return; // wait for the map; never swap identity inside a mission
                    }
                    _pendingClaim = false;
                    if (PeerDetection.IsClient() == true)
                    {
                        return; // BT assigns the client's hero through its own claim flow
                    }
                    Claim();
                    return;
                }
                MaintainRecord();
            }
            catch (Exception ex)
            {
                _pendingClaim = false;
                Log.Info("[IDENTITY] claim error: " + ex.Message);
            }
        }

        private static void Claim()
        {
            string campaignId = Campaign.Current.UniqueGameId;
            if (string.IsNullOrEmpty(campaignId))
            {
                Log.Info("[IDENTITY] campaign has no UniqueGameId — identity lock inactive for this game");
                return;
            }
            Dictionary<string, string> map = LoadMap();
            string recordedId;
            map.TryGetValue(campaignId, out recordedId);

            Hero target = null;
            string source = null;
            if (!string.IsNullOrEmpty(recordedId))
            {
                target = FindHeroById(recordedId);
                source = "recorded";
                if (target == null || !target.IsAlive)
                {
                    Log.Info("[IDENTITY] recorded hero '" + recordedId + "' is " + (target == null ? "gone" : "dead") +
                             " — keeping the save's player (" + SafeName(Hero.MainHero) + "); the record will follow your next hero");
                    map[campaignId] = Hero.MainHero.StringId;
                    SaveMap(map);
                    _claimedThisCampaign = true;
                    return;
                }
            }
            else
            {
                string configured = GuardConfig.String("myHero", "");
                if (!string.IsNullOrEmpty(configured))
                {
                    target = FindHeroByName(configured);
                    source = "guardconfig myHero";
                    if (target == null)
                    {
                        Log.Info("[IDENTITY] myHero=\"" + configured + "\" matched no living hero — check the spelling (hero names, case-insensitive)");
                        return;
                    }
                }
                else if (IsBrandNewCampaign())
                {
                    map[campaignId] = Hero.MainHero.StringId;
                    SaveMap(map);
                    _claimedThisCampaign = true;
                    Log.Info("[IDENTITY] new campaign — recorded " + SafeName(Hero.MainHero) + " as this machine's hero");
                    return;
                }
                else
                {
                    if (!_guidanceLogged)
                    {
                        _guidanceLogged = true;
                        Log.Info("[IDENTITY] no hero recorded for this campaign on this machine — if this is a shared co-op save, set \"myHero\": \"YourHeroName\" in guardconfig.json once and the map maintains itself afterwards");
                    }
                    return;
                }
            }

            if (target == Hero.MainHero)
            {
                map[campaignId] = target.StringId;
                SaveMap(map);
                _claimedThisCampaign = true;
                return; // already the right hero — just (re)record
            }
            Hero previous = Hero.MainHero;
            ChangePlayerCharacterAction.Apply(target);
            map[campaignId] = target.StringId;
            SaveMap(map);
            _claimedThisCampaign = true;
            SelfHealing.RecordFire("hero-identity-lock");
            Log.Info("[IDENTITY] switched player to " + SafeName(target) + " (" + source + ") — the save was last played as " + SafeName(previous));
            Log.Screen("playing as " + SafeName(target) + " — this machine's hero (save was last played as " + SafeName(previous) + ")");
        }

        /// <summary>Follow death-succession: when the recorded hero died and play continued
        /// as the heir, the record moves to the new MainHero. A LIVING recorded hero that
        /// differs from MainHero is never clobbered (that is a foreign or cheat switch).</summary>
        private static void MaintainRecord()
        {
            int now = Environment.TickCount;
            if (!_claimedThisCampaign || Campaign.Current == null || Hero.MainHero == null ||
                (_lastMaintainTick != 0 && now - _lastMaintainTick < 60000 && now >= _lastMaintainTick))
            {
                return;
            }
            _lastMaintainTick = now;
            if (PeerDetection.IsClient() == true)
            {
                return;
            }
            string campaignId = Campaign.Current.UniqueGameId;
            if (string.IsNullOrEmpty(campaignId))
            {
                return;
            }
            Dictionary<string, string> map = LoadMap();
            string recordedId;
            if (!map.TryGetValue(campaignId, out recordedId) || recordedId == Hero.MainHero.StringId)
            {
                return;
            }
            Hero recorded = FindHeroById(recordedId);
            if (recorded == null || !recorded.IsAlive)
            {
                map[campaignId] = Hero.MainHero.StringId;
                SaveMap(map);
                Log.Info("[IDENTITY] recorded hero died — this machine's hero is now " + SafeName(Hero.MainHero) + " (succession)");
            }
        }

        private static bool IsBrandNewCampaign()
        {
            try
            {
                // A campaign younger than a day has never been saved-and-shared; recording
                // its creator is unambiguous.
                CampaignTime start = Campaign.Current.Models.CampaignTimeModel.CampaignStartTime;
                return CampaignTime.Now.ToDays - start.ToDays < 1f;
            }
            catch
            {
                return false;
            }
        }

        private static Hero FindHeroById(string stringId)
        {
            return Hero.FindFirst(h => h != null && h.StringId == stringId);
        }

        private static Hero FindHeroByName(string name)
        {
            Hero clanMatch = null, anyMatch = null;
            foreach (Hero hero in Hero.AllAliveHeroes)
            {
                if (hero == null || hero.Name == null ||
                    !string.Equals(hero.Name.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Hero.MainHero != null && hero.Clan == Hero.MainHero.Clan)
                {
                    clanMatch = clanMatch ?? hero;
                }
                anyMatch = anyMatch ?? hero;
            }
            return clanMatch ?? anyMatch;
        }

        private static string SafeName(Hero hero)
        {
            try { return hero != null && hero.Name != null ? hero.Name.ToString() : "?"; }
            catch { return "?"; }
        }

        // ---- per-machine storage (flat JSON, regex-parsed like GuardConfig) --------------

        internal static Dictionary<string, string> ParseMap(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
            {
                return map;
            }
            foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
            {
                map[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return map;
        }

        internal static string FormatMap(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            bool first = true;
            foreach (KeyValuePair<string, string> kv in map)
            {
                if (!first)
                {
                    sb.AppendLine(",");
                }
                first = false;
                sb.Append("  \"").Append(kv.Key).Append("\": \"").Append(kv.Value).Append("\"");
            }
            sb.AppendLine();
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static Dictionary<string, string> LoadMap()
        {
            try
            {
                return File.Exists(MapPath) ? ParseMap(File.ReadAllText(MapPath)) : new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static void SaveMap(Dictionary<string, string> map)
        {
            try
            {
                File.WriteAllText(MapPath, FormatMap(map));
            }
            catch (Exception ex)
            {
                Log.Info("[IDENTITY] could not persist hero-identity.json: " + ex.Message);
            }
        }

        private static SelfHealing.TestResult SelfTest()
        {
            // Storage round-trip + the claim prerequisites resolving.
            var probe = new Dictionary<string, string> { { "campaign_a", "hero_1" }, { "campaign_b", "hero_2" } };
            Dictionary<string, string> parsed = ParseMap(FormatMap(probe));
            bool roundTrip = parsed.Count == 2 && parsed["campaign_a"] == "hero_1" && parsed["campaign_b"] == "hero_2";
            bool actionExists = HarmonyLib.AccessTools.Method(typeof(ChangePlayerCharacterAction), "Apply") != null;
            bool pass = roundTrip && actionExists;
            return SelfHealing.TestResult.Of("hero-identity-lock.contract", pass,
                pass ? "map round-trips; ChangePlayerCharacterAction resolves"
                     : "roundTrip=" + roundTrip + " actionExists=" + actionExists);
        }
    }
}
