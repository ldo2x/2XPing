using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // Deadlock's datacenter table and player numbers, checked against saved answers (tests/data):
    //   sdr-deadlock.json   GetSDRConfig appid 1422450, revision 1791863345 (2026-10-13)
    //   active-matches.json deadlock-api /v1/matches/active at 2026-10-13 12:46:18 UTC (players lists removed)
    static partial class Tests
    {
        const long ActiveSampleAt = 1791895578; // the sample's Date header, as UTC seconds

        static string Data(string name)
        {
            return File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "data", name));
        }

        static GameDb.ValveConfig SavedSdr()
        {
            return GameDb.ParseValve(Data("sdr-deadlock.json"), new Dictionary<string, int>());
        }

        // Deadlock as the app loads it, from the saved config instead of the network.
        static Game SavedDeadlock()
        {
            var g = GameDb.All().First(x => x.Name == "Deadlock");
            var cfg = GameDb.ParseValve(Data("sdr-deadlock.json"), g.Backbone);
            g.Regions = cfg.Regions;
            g.NoRelayHosts = GameDb.NoRelayHostsOf(g, cfg);
            g.RegionMap = RegionGroups.DeadlockMap(g);
            g.Routes = SdrRoutes.Build(g.Backbone);
            return g;
        }

        static void DeadlockTableKnowsEveryPopOfTheConfig()
        {
            var cfg = SavedSdr();
            Check(cfg.Revision == "1791863345", "revision: " + cfg.Revision);
            Check(cfg.PopIds.Count == 37, "37 datacenters, not " + cfg.PopIds.Count);
            var unknown = DeadlockData.UnknownPops(cfg.PopIds);
            Check(unknown.Count == 0, "every datacenter is in the table: " + string.Join(",", unknown.ToArray()));
            Check(DeadlockData.Hosting.Keys.All(k => !DeadlockData.RelayOnly.Contains(k)), "no datacenter is both hosting and relay-only");
            // the four fixes of the audit (B2)
            Check(DeadlockData.Hosting["jnb"] == "Europe", "Johannesburg hosts the Europe pool");
            foreach (var id in new[] { "bom", "bom2", "maa", "maa2", "gum", "dxb", "dfjr" })
                Check(!DeadlockData.Hosting.ContainsKey(id) && DeadlockData.RelayOnly.Contains(id), id + " is relay-only");
            foreach (var id in new[] { "hel", "fsn", "eat", "mwh", "dsto", "dlhr" })
                Check(DeadlockData.Hosting.ContainsKey(id), id + " is a known host");
            Check(!DeadlockData.Hosting.ContainsKey(DeadlockData.Russia) && !DeadlockData.Hosting.ContainsValue(DeadlockData.Russia),
                  "no datacenter maps to Russia");
            // a new datacenter is noticed, once, in a fixed order
            var news = DeadlockData.UnknownPops(new[] { "ams", "zzz", "abc", "zzz", null, "" });
            Check(news.SequenceEqual(new[] { "abc", "zzz" }), "unknown: " + string.Join(",", news.ToArray()));
            Check(Issues.Codes.ContainsKey("sdr_new_pop"), "the new-datacenter problem has a code");
        }

        static void DeadlockConfigSplitsRelaysAndNoRelayHosts()
        {
            var cfg = SavedSdr();
            var ids = cfg.Regions.Select(r => r.Id).ToList();
            var none = cfg.NoRelay.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check(none.SequenceEqual(new[] { "dlhr", "dsto", "eat", "fsn", "hel" }), "no relays: " + string.Join(",", none.ToArray()));
            Check(cfg.Regions.Count == 32 && cfg.Regions.All(r => r.Endpoints.Count > 0 && !r.NoRelay), "32 datacenters with relays");
            Check(cfg.NoRelay.All(r => r.NoRelay && r.Endpoints.Count == 0 && r.Cidrs.Count == 0), "no-relay rows have nothing to ping or block");
            Check(cfg.Regions.Sum(r => r.Cidrs.Count) == 146, "146 relay addresses, not " + cfg.Regions.Sum(r => r.Cidrs.Count));
            Check(cfg.Regions.SelectMany(r => r.Cidrs).All(RemoteAddress.Valid), "every relay address is a valid firewall address");
            Check(ids.Contains("dfjr") && ids.Contains("jnb") && ids.Contains("dxb"), "dfjr, jnb and dxb have relays");

            var g = SavedDeadlock();
            var hostsNoRelay = g.NoRelayHosts.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check(hostsNoRelay.SequenceEqual(new[] { "dlhr", "dsto", "eat", "fsn", "hel" }), "can't-be-locked rows: " + string.Join(",", hostsNoRelay.ToArray()));
            Check(g.NoRelayHosts.All(r => r.Flag != null), "no-relay rows have flags");
            Check(g.Regions.All(r => !r.NoRelay), "no-relay datacenters stay out of the lockable list");
        }

        static void DeadlockRegionsFromSavedConfig()
        {
            var g = SavedDeadlock();
            var eu = Def(g, "Europe");
            Check(eu != null && eu.Ids.Contains("jnb") && eu.Ids.Contains("fra"), "Europe has jnb and fra");
            var sea = Def(g, "SEAsia");
            Check(sea != null && sea.Ids.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "hkg", "seo", "sgp", "tyo" }),
                  "SE Asia is sgp, hkg, tyo, seo: " + (sea == null ? "-" : string.Join(",", sea.Ids)));
            Check(Def(g, DeadlockData.Russia) == null, "Russia is never a tile");
            foreach (var d in g.RegionMap)
                foreach (var id in new[] { "dxb", "dfjr", "bom2", "maa2", "gum", "hel", "fsn", "eat", "dsto", "dlhr" })
                    Check(!d.Ids.Contains(id), d.Key + " must not contain " + id);
            // a tile's members are what its lock picks: never a relay-only or a no-relay datacenter
            var all = RegionGroups.Build(g, null, null, r => 50);
            var picks = RegionGroups.PickIds(g, all);
            Check(!picks.Contains("dxb") && !picks.Contains("bom2") && !picks.Contains("hel") && picks.Contains("jnb"), "picks: " + string.Join(",", picks.ToArray()));
        }

        static void DeadlockCountsRegionsFromActiveMatches()
        {
            var c = DeadlockData.CountRegions(Data("active-matches.json"), ActiveSampleAt);
            var expect = new Dictionary<string, int> { { "Europe", 70 }, { "Russia", 27 }, { "Row", 24 }, { "SEAsia", 18 }, { "Oceania", 10 }, { "SAmerica", 1 } };
            Check(c.Count == expect.Count && expect.All(kv => c.ContainsKey(kv.Key) && c[kv.Key] == kv.Value),
                  "saved sample: " + string.Join(", ", c.Select(kv => kv.Key + " " + kv.Value).ToArray()));
            Check(c.Keys.All(k => Stats.MmRegions.ContainsKey(k)), "every key has a display name");
            // an hour later none of them started in the last hour
            Check(DeadlockData.CountRegions(Data("active-matches.json"), ActiveSampleAt + 7200).Count == 0, "two hours later: nothing");

            // the edges: the hour's border, a match listed twice, the parsed name without the number, unknown modes
            string json = "[" +
                "{\"match_id\":1,\"start_time\":1000,\"region_mode\":1}," +
                "{\"match_id\":1,\"start_time\":1000,\"region_mode\":1}," +                   // listed twice
                "{\"match_id\":2,\"start_time\":401,\"region_mode\":4}," +                    // 3599 s ago
                "{\"match_id\":3,\"start_time\":400,\"region_mode\":2}," +                    // exactly an hour ago: out
                "{\"match_id\":4,\"start_time\":900,\"region_mode\":null,\"region_mode_parsed\":\"se_asia\"}," +
                "{\"match_id\":5,\"start_time\":900,\"region_mode\":9}," +                    // unknown mode
                "{\"match_id\":6,\"region_mode\":0}," +                                       // no start time
                "{\"match_id\":7,\"start_time\":950,\"region_mode\":5}]";
            c = DeadlockData.CountRegions(json, 4000);
            Check(c.Count == 4 && c["Europe"] == 1 && c["Russia"] == 1 && c["SEAsia"] == 1 && c["Oceania"] == 1,
                  "edges: " + string.Join(", ", c.Select(kv => kv.Key + " " + kv.Value).ToArray()));
            bool threw = false;
            try { DeadlockData.CountRegions("{\"error\":\"rate limited\"}", 4000); } catch (FormatException) { threw = true; }
            Check(threw, "an answer that isn't a list is an error, not zero matches");
        }

        // Shared numbers are only used when Supabase counted them from the live list: the old /v1/sql count is on
        // another scale (about 2.6 times higher) and would make every wait look shorter.
        static void DeadlockSharedRegionsNeedTheMarker()
        {
            var regions = new Dictionary<string, object> { { "Europe", 70 }, { "Russia", "27" }, { "Row", null } };
            var row = new Dictionary<string, object> { { "regions", regions }, { "regions_source", "active" } };
            var got = DeadlockData.SharedRegions(row);
            Check(got != null && got.Count == 2 && got["Europe"] == 70 && got["Russia"] == 27, "marked row is used");
            row["regions_source"] = null;
            Check(DeadlockData.SharedRegions(row) == null, "no marker (old /v1/sql job): ignored");
            row["regions_source"] = "sql";
            Check(DeadlockData.SharedRegions(row) == null, "another source: ignored");
            row.Remove("regions_source");
            Check(DeadlockData.SharedRegions(row) == null, "column missing (SQL not applied yet): ignored");
            Check(DeadlockData.SharedRegions(null) == null, "no row");
            Check(DeadlockData.SharedRegions(new Dictionary<string, object> { { "regions_source", "active" }, { "regions", null } }) == null, "no regions");
        }

        // A saved pick of a datacenter that no longer hosts matches (India became relay-only) is dropped at load.
        static void StalePicksAreDropped()
        {
            var g = SavedDeadlock();
            var picks = new HashSet<string> { "fra", "bom2", "dxb" };
            Check(GameDb.PickedAtLoad(g, g.Regions.First(r => r.Id == "fra"), picks), "fra stays picked");
            Check(!GameDb.PickedAtLoad(g, g.Regions.First(r => r.Id == "bom2"), picks), "bom2 is relay-only now: dropped");
            Check(!GameDb.PickedAtLoad(g, g.Regions.First(r => r.Id == "dxb"), picks), "dxb never picked");
            Check(!GameDb.PickedAtLoad(g, g.Regions.First(r => r.Id == "ams"), picks), "not saved: not picked");
            var cs2 = new Game { Name = "CS2" }; // no hosting list: every datacenter can be picked
            Check(GameDb.PickedAtLoad(cs2, new Region { Id = "dxb" }, picks), "games without a hosting list keep every pick");
        }

        static void Ipv6AddressValidation()
        {
            foreach (var ok in new[] { "2001:db8::1", "2001:db8::/32", "::/0", "2001:db8::1/128", "fe80::1", "2001:db8::1-2001:db8::9",
                                       "2001:db8::1-2001:db8::1", "::ffff:1.2.3.4", "2001:0db8:0000:0000:0000:0000:0000:0001" })
                Check(RemoteAddress.Valid(ok), "valid: " + ok);
            foreach (var bad in new[] { "2001:db8::/129", "2001:db8::9-2001:db8::1", "2001:db8::1-1.2.3.4", "gggg::1", "2001:db8::1%12",
                                        "[2001:db8::1]", "2001:db8::1 ", " 2001:db8::1", "2001:db8::1,2001:db8::2", "1:2:3:4:5:6:7:8:9",
                                        "2001:db8::1\n", "١::1", "2001:db8::/٣٢", ":", "1.2.3.4-2001:db8::1" })
                Check(!RemoteAddress.Valid(bad), "invalid: " + bad.Replace("\n", "\\n"));
            int skipped;
            var list = ServerLock.ValidAddresses(new[] { "1.2.3.4/32", "2001:db8::1/128", "2001:db8::1/128", "2001:db8::1%3", "5.6.7.8" }, out skipped);
            Check(list.SequenceEqual(new[] { "1.2.3.4/32", "2001:db8::1/128", "5.6.7.8" }) && skipped == 1,
                  "IPv4 and IPv6 kept in one list: " + string.Join(",", list.ToArray()) + " skipped " + skipped);
            Check(Detector.InCidr("2001:db8::5", "2001:db8::/32") && !Detector.InCidr("2001:db9::5", "2001:db8::/32"), "IPv6 prefix match");
            Check(Detector.InCidr("2001:db8::1", "2001:db8::1/128") && !Detector.InCidr("2001:db8::2", "2001:db8::1/128"), "IPv6 /128");
            Check(!Detector.InCidr("1.2.3.4", "2001:db8::/32") && !Detector.InCidr("2001:db8::1", "1.2.3.0/24"), "kinds never mix");
            Check(Detector.InCidr("1.2.3.4", "1.2.3.0/24"), "IPv4 still matches");
        }

        static void Ipv6RelaysAreParsedAndBlocked()
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = (Dictionary<string, object>)js.DeserializeObject(Data("sdr-deadlock.json"));
            var pops = (Dictionary<string, object>)root["pops"];
            // fra: an IPv6 address next to each IPv4 one (and one broken); a new datacenter with IPv6 relays only
            var fra = (Dictionary<string, object>)pops["fra"];
            int n = 0;
            foreach (Dictionary<string, object> relay in (object[])fra["relays"]) relay["ipv6"] = n++ == 0 ? "2001:db8::bad::1" : "2602:801:f000::" + n.ToString("x");
            pops["v6only"] = new Dictionary<string, object> { { "desc", "Only IPv6" }, { "relays", new object[] {
                new Dictionary<string, object> { { "ipv6", "2602:801:f001::1" }, { "port_range", new object[] { 27015, 27060 } } } } } };
            var cfg = GameDb.ParseValve(js.Serialize(root), new Dictionary<string, int>());
            var f = cfg.Regions.First(r => r.Id == "fra");
            int v6 = f.Cidrs.Count(c => c.Contains(":"));
            Check(v6 == 13 && f.Cidrs.Count == 27, "fra: 14 IPv4 + 13 good IPv6 addresses, got " + f.Cidrs.Count + " (" + v6 + " IPv6)");
            Check(f.Cidrs.Where(c => c.Contains(":")).All(c => c.EndsWith("/128") && RemoteAddress.Valid(c)), "IPv6 relays as valid /128 entries");
            Check(f.Endpoints.All(e => !e.Host.Contains(":")), "fra is pinged on IPv4");
            var only = cfg.Regions.FirstOrDefault(r => r.Id == "v6only");
            Check(only != null && only.Cidrs.SequenceEqual(new[] { "2602:801:f001::1/128" }) && only.Endpoints.Count == 1 && only.Endpoints[0].Host == "2602:801:f001::1",
                  "a datacenter with IPv6 relays only is kept, blocked and pinged on IPv6");
            Check(DeadlockData.UnknownPops(cfg.PopIds).SequenceEqual(new[] { "v6only" }), "and noticed as new");
            // the lock's list takes them as they are
            int skipped;
            var block = ServerLock.ValidAddresses(f.Cidrs, out skipped);
            Check(block.Count == 27 && skipped == 0, "all 27 go into the firewall rule");
        }

        static void CardRussiaFootnote()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("Europe", "Europe", "أوروبا", 78, true));
            Check(CardLogic.Decide(i).Note == null, "no footnote unless asked (other games)");
            i.RussiaNote = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Choose && v.Note == "روسيا لها كيو خاص يلعب على سيرفرات أوروبا، والتثبيت ما يقدر يختارها.", "Arabic, no number: " + v.Note);
            i.RussiaPlayers = 4321;
            v = CardLogic.Decide(i);
            Check(v.Note == "روسيا لها كيو خاص يلعب على سيرفرات أوروبا، والتثبيت ما يقدر يختارها (تقريباً 4,321 لاعب).", "Arabic: " + v.Note);
            Lang.Ar = false;
            v = CardLogic.Decide(i);
            Check(v.Note == "Russia is a separate queue played on the European servers; the lock can't choose it (~4,321 players).", "English: " + v.Note);
            // the footnote belongs to the region choices: never on the other states
            i.LockedNames = "Europe";
            Check(CardLogic.Decide(i).Note == null, "locked: no footnote");
            i.LockedNames = null;
            i.Choices = new List<RegionChoice>();
            Check(CardLogic.Decide(i).Note == null, "no tiles: no footnote");
            Lang.Ar = true;
        }

        static void RunDeadlockTests()
        {
            Run("DeadlockTableKnowsEveryPopOfTheConfig", DeadlockTableKnowsEveryPopOfTheConfig);
            Run("DeadlockConfigSplitsRelaysAndNoRelayHosts", DeadlockConfigSplitsRelaysAndNoRelayHosts);
            Run("DeadlockRegionsFromSavedConfig", DeadlockRegionsFromSavedConfig);
            Run("DeadlockCountsRegionsFromActiveMatches", DeadlockCountsRegionsFromActiveMatches);
            Run("DeadlockSharedRegionsNeedTheMarker", DeadlockSharedRegionsNeedTheMarker);
            Run("StalePicksAreDropped", StalePicksAreDropped);
            Run("Ipv6AddressValidation", Ipv6AddressValidation);
            Run("Ipv6RelaysAreParsedAndBlocked", Ipv6RelaysAreParsedAndBlocked);
            Run("CardRussiaFootnote", CardRussiaFootnote);
            RunRouteTests();
        }
    }
}
