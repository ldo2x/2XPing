using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    static partial class Tests
    {
        static RegionChoice ZoneChoice(string key, string en, string ar, string zone, bool selected)
        {
            return new RegionChoice { Def = RegionDef.Of(key, en, ar).In(zone), Ping = 50, Selected = selected };
        }

        static void OverwatchRegionsHaveZones()
        {
            var g = Find("Overwatch 2");
            foreach (var d in g.RegionMap) Check(d.Zone != null, d.Key + " has no zone");
            Check(Def(g, "na").Zone == Zones.Americas && Def(g, "sa").Zone == Zones.Americas, "Americas = North and South America");
            Check(Def(g, "eu").Zone == Zones.Europe && Def(g, "me").Zone == Zones.Europe, "Europe = Europe and Middle East");
            Check(Def(g, "ea").Zone == Zones.Asia && Def(g, "sg").Zone == Zones.Asia && Def(g, "au").Zone == Zones.Asia, "Asia = East Asia, Singapore, Australia");
            Check(Zones.Of(g, "GSG1") == Zones.Asia && Zones.Of(g, "GMEC2") == Zones.Europe && Zones.Of(g, "ORD1") == Zones.Americas, "datacenter zones");
            Check(Zones.Of(g, "nope") == null && Zones.Of(g, null) == null, "unknown datacenter has no zone");
            var dbd = Find("Dead by Daylight");
            Check(dbd.RegionMap.All(d => d.Zone == null) && Zones.Of(dbd, "eu-central-1") == null, "other games have no zones");
            Lang.Ar = true;
            Check(Zones.Name(Zones.Americas) == "أمريكا" && Zones.Name(Zones.Europe) == "أوروبا" && Zones.Name(Zones.Asia) == "آسيا", "Arabic zone names");
            Lang.Ar = false;
            Check(Zones.Name(Zones.Americas) == "Americas" && Zones.Name(Zones.Asia) == "Asia", "English zone names");
            Lang.Ar = true;
        }

        static void ZonesRememberWhenSeen()
        {
            var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
            long t = now.Ticks;
            Check(Zones.Add(null, "am", now) == "am:" + t, "first zone: " + Zones.Add(null, "am", now));
            string two = Zones.Add("am:" + t, "as", now);
            Check(two == "am:" + t + ",as:" + t, "second zone: " + two);
            var later = now.AddDays(3);
            Check(Zones.Add(two, "am", later) == "am:" + later.Ticks + ",as:" + t, "seeing a zone again moves its time");
            Check(Zones.Add("am:" + t, null, later) == "am:" + t, "a datacenter without a zone adds nothing");
            Check(Zones.Add("am:" + t + ",junk:5,xx", "xx", now) == "am:" + t, "unknown entries dropped");
            Check(Zones.Add("eu:notanumber", null, now) == "", "a broken time is dropped");
            Check(Zones.Parse(null, now).Count == 0 && Zones.Parse("", now).Count == 0, "nothing saved");

            // the old plain list counts as seen now (migration), saved with the time
            var old = Zones.Parse(" eu , as,,eu", now);
            Check(old.Count == 2 && old["eu"] == now && old["as"] == now, "plain list migrates to now");
            Check(Zones.Format(old) == "eu:" + t + ",as:" + t, "migrated form: " + Zones.Format(old));
            Check(Zones.Recent("eu,as", now).SequenceEqual(new[] { "eu", "as" }), "migrated zones are fresh");

            // only the last 14 days count for the warning
            string saved = "am:" + now.AddDays(-20).Ticks + ",eu:" + now.AddDays(-13).Ticks;
            var recent = Zones.Recent(saved, now);
            Check(recent.Count == 1 && recent[0] == "eu", "20 days ago is stale, 13 days ago counts: " + string.Join(",", recent.ToArray()));
            Check(Zones.Recent(saved, now.AddDays(2)).Count == 0, "both stale two days later");
            Check(Zones.Recent("as:" + now.AddDays(-14).Ticks, now).Count == 1, "exactly 14 days still counts");
        }

        static void ZoneWarningText()
        {
            Lang.Ar = true;
            var sg = ZoneChoice("sg", "Singapore", "سنغافورة", Zones.Asia, true);
            var am = new List<string> { Zones.Americas };
            string w = Zones.Warning(new List<RegionChoice> { sg }, am);
            Check(w == "سنغافورة في منطقة آسيا، وإنت تلعب على أمريكا. ما بتلقى مباراة إلا لو كانت منطقة لعبتك آسيا.", "Arabic: " + w);
            Lang.Ar = false;
            w = Zones.Warning(new List<RegionChoice> { sg }, am);
            Check(w == "Singapore is in Asia, but you play on Americas. You'll only find a match if the game's own region is Asia.", "English: " + w);

            // two regions, two zones, two zones seen
            var eu = ZoneChoice("eu", "Europe", "أوروبا", Zones.Europe, true);
            var ea = ZoneChoice("ea", "East Asia", "شرق آسيا", Zones.Asia, true);
            w = Zones.Warning(new List<RegionChoice> { ea, sg }, am);
            Check(w == "East Asia and Singapore are in Asia, but you play on Americas. You'll only find a match if the game's own region is Asia.", "English two in one zone: " + w);
            Lang.Ar = true;
            var na = ZoneChoice("na", "North America", "أمريكا الشمالية", Zones.Americas, true);
            w = Zones.Warning(new List<RegionChoice> { na, sg }, new List<string> { Zones.Europe });
            Check(w == "أمريكا الشمالية في منطقة أمريكا وسنغافورة في منطقة آسيا، وإنت تلعب على أوروبا. ما بتلقى مباراة إلا لو كانت منطقة لعبتك أمريكا أو آسيا.", "Arabic two zones: " + w);
            w = Zones.Warning(new List<RegionChoice> { sg }, new List<string> { Zones.Americas, Zones.Europe });
            Check(w == "سنغافورة في منطقة آسيا، وإنت تلعب على أمريكا وأوروبا. ما بتلقى مباراة إلا لو كانت منطقة لعبتك آسيا.", "Arabic two seen: " + w);

            // no warning: nothing seen yet, one picked region in a seen zone, nothing picked, or a game without zones
            Check(Zones.Warning(new List<RegionChoice> { sg }, new List<string>()) == null, "nothing seen: no warning");
            Check(Zones.Warning(new List<RegionChoice> { sg, eu }, new List<string> { Zones.Europe }) == null, "one region in a seen zone: no warning");
            Check(Zones.Warning(new List<RegionChoice>(), am) == null, "nothing picked: no warning");
            Check(Zones.Warning(new List<RegionChoice> { Choice("x", "X", "X", 10, true) }, am) == null, "no zone: no warning");
        }

        static void CardZoneWarning()
        {
            Lang.Ar = true;
            var i = Lockable(ZoneChoice("sg", "Singapore", "سنغافورة", Zones.Asia, true), ZoneChoice("na", "North America", "أمريكا الشمالية", Zones.Americas, false));
            i.ZoneWarning = "تحذير";
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Choose && v.Sub == "تحذير", "Choose sub is the warning: " + v.Sub);
            Check(v.Button == "العب على سنغافورة", "button still locks: " + v.Button);
            i.ZoneWarning = null;
            Check(CardLogic.Decide(i).Sub == null, "no warning: no sub");
            // nothing selected: the "pick one" hint wins
            var none = Lockable(ZoneChoice("sg", "Singapore", "سنغافورة", Zones.Asia, false));
            none.ZoneWarning = "تحذير";
            v = CardLogic.Decide(none);
            Check(v.Sub == "اختار منطقة وحدة على الأقل" && v.Button == null, "nothing selected keeps its hint: " + v.Sub);
            // locked: the warning is for choosing only
            i.ZoneWarning = "تحذير";
            i.LockedNames = "سنغافورة";
            Check(CardLogic.Decide(i).Sub != "تحذير", "locked card has no zone warning");
        }

        static void CardNoMatchYet()
        {
            Lang.Ar = true;
            var i = Lockable(ZoneChoice("sg", "Singapore", "سنغافورة", Zones.Asia, true));
            i.LockedNames = "سنغافورة";
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Locked && !v.Sub.Contains("ما لقيت مباراة"), "no hint before 3 minutes");
            i.NoMatchYet = true;
            v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Locked && v.Sub == "افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول. ما لقيت مباراة؟ يمكن المنطقة اللي اخترتها مو من مناطق لعبتك.", "Arabic: " + v.Sub);
            Lang.Ar = false;
            v = CardLogic.Decide(i);
            Check(v.Sub.EndsWith(" No match found? The region you picked may be outside your game's region."), "English: " + v.Sub);
            Lang.Ar = true;
            // in a match: the match card, never the hint
            i.MatchRegion = "سنغافورة";
            i.MatchOnPicked = true;
            v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.InMatchOk && !v.Sub.Contains("ما لقيت مباراة"), "in a match: no hint");
        }

        // The block list LockServers applies, with only the given datacenters picked.
        static List<string> OverwatchBlock(Game g, params string[] picked)
        {
            var regions = g.Regions.Where(r => picked.Contains(r.Id)).ToList();
            return LockPlanner.Plan(g, regions, "Overwatch.exe", new List<string>(), IpLists.Resolve).Block;
        }

        static void OverwatchServiceHostsStayOpen()
        {
            var g = Find("Overwatch 2");
            var block = OverwatchBlock(g, "GSG1");
            Check(!LockTest.Blocks(block, "66.40.191.12"), "66.40.191.12 (login, TCP 3724) stays open");
            Check(!LockTest.Blocks(block, "137.221.104.171"), "137.221.104.171 (TCP 443) stays open");
            Check(LockTest.Blocks(block, "137.221.69.103"), "Chicago 137.221.69.103 is blocked");
            Check(LockTest.Blocks(block, "66.40.190.5") && LockTest.Blocks(block, "137.221.105.5"), "the rest of those datacenter blocks is still blocked");
            Check(!LockTest.Blocks(block, "64.224.3.1"), "Irvine stays open");
            Check(g.DatacenterOf("66.40.191.12") == null && g.DatacenterOf("137.221.104.171") == null, "service hosts are no datacenter");
            var ord = g.DatacenterOf("137.221.69.103");
            Check(ord != null && ord.Id == "ORD1", "137.221.69.103 is Chicago");
            foreach (var r in g.Regions)
                Check(!IpLists.Contains(r.Cidrs, "66.40.191.12") && !IpLists.Contains(r.Cidrs, "137.221.104.171"), r.Id + " lists a service host");
        }

        static void OverwatchVoiceChatIgnored()
        {
            var g = Find("Overwatch 2");
            Check(g.Ignored("85.236.96.10") && g.Ignored("85.236.103.250"), "Vivox 85.236.96.0/21 is ignored");
            Check(!g.Ignored("85.236.104.1") && !g.Ignored("137.221.69.103"), "outside Vivox is not ignored");
            Check(g.DatacenterOf("85.236.97.1") == null, "Vivox is no datacenter");
            var hits = new List<Detector.Hit>
            {
                new Detector.Hit { Ip = "85.236.97.1", Port = 12000, Packets = 900 },
                new Detector.Hit { Ip = "137.221.104.171", Port = 3724, Packets = 50 },
                new Detector.Hit { Ip = "137.221.69.103", Port = 26503, Packets = 400 },
            };
            var server = MatchClassifier.ServerHits(g, hits);
            Check(server.Count == 1 && server[0].Ip == "137.221.69.103", "only the match server is left: " + string.Join(",", server.Select(h => h.Ip).ToArray()));
            Check(MatchClassifier.ServerHits(g, new List<Detector.Hit> { hits[0] }).Count == 0, "voice chat alone = not in a match (no unknown_server)");
            // Irvine (the menu and Practice Range host, always allowed) is no match server either
            Check(g.Ignored("64.224.0.10") && g.Ignored("64.224.7.200") && !g.Ignored("64.224.8.1"), "Irvine 64.224.0.0/21 is ignored");
            var menu = new List<Detector.Hit> { new Detector.Hit { Ip = "64.224.3.9", Port = 1119, Packets = 300 }, hits[0] };
            Check(MatchClassifier.ServerHits(g, menu).Count == 0, "menu traffic to Irvine + voice chat = not in a match");
            Check(g.LockAllow.All(c => g.DetectIgnore.Contains(c)), "everything the lock leaves open is ignored by the server check");
            var dbd = Find("Dead by Daylight");
            Check(!dbd.Ignored("85.236.97.1"), "other games ignore nothing");
        }

        static void MinimizedTitle()
        {
            Check(MainForm.TitleFor("Normal", true, "locked on Europe · 78 ms") == Brand.Caption + " · locked on Europe · 78 ms", "minimized: live status in the title");
            Check(MainForm.TitleFor("Normal", false, "locked on Europe · 78 ms") == "Normal", "restored: the normal title");
            Check(MainForm.TitleFor("Normal", true, null) == "Normal", "minimized, nothing running: the normal title");
        }

        static void ZonesLearnFromServerChecks()
        {
            var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
            // games without zones (Valve, a missing setting): nothing to save, never a crash
            var valve = new Game { Name = "Valve-like", Provider = Provider.Valve };
            Check(Zones.Learn(valve, null, new[] { "fra", "dxb" }, now) == null, "no RegionMap, null setting: nothing to save");
            var dbd = Find("Dead by Daylight");
            Check(Zones.Learn(dbd, null, new[] { "eu-central-1" }, now) == null, "regions without zones: nothing to save");
            Check(Zones.Learn(dbd, "am:" + now.Ticks, new[] { "eu-central-1" }, now) == null, "a saved list stays as it is");
            Check(Zones.Learn(null, null, new[] { "x" }, now) == null && Zones.Learn(valve, null, null, now) == null, "nulls");
            var ow = Find("Overwatch 2");
            Check(Zones.Learn(ow, null, new string[0], now) == null, "no datacenter seen: nothing to save");
            Check(Zones.Learn(ow, null, new[] { "GSG1" }, now) == "as:" + now.Ticks, "first zone saved");
            Check(Zones.Learn(ow, "as:" + now.Ticks, new[] { "GSG1" }, now.AddMinutes(30)) == null, "seen again within the hour: no write");
            var later = now.AddHours(2);
            Check(Zones.Learn(ow, "as:" + now.Ticks, new[] { "GSG1", "ORD1" }, later) == "am:" + later.Ticks + ",as:" + later.Ticks, "an hour later: times move, new zone added");
            Check(Zones.Learn(ow, "as:" + now.Ticks, new[] { "nope" }, later) == null, "an unknown datacenter: nothing to save");
        }

        static void NoMatchCountsWatchedTimeOnly()
        {
            var t0 = new DateTime(2026, 10, 13, 12, 0, 0);
            var seen = TimeSpan.Zero;
            seen = Zones.AddObserved(seen, DateTime.MinValue, t0);
            Check(seen == TimeSpan.Zero, "the first check adds nothing");
            // 30 s checks for 3 min 30 s while the game runs and the app is in front
            var last = t0;
            for (int i = 1; i <= 7; i++) { var at = t0.AddSeconds(30 * i); seen = Zones.AddObserved(seen, last, at); last = at; }
            Check(seen == TimeSpan.FromSeconds(210), "watched 3:30: " + seen);
            Check(Zones.NoMatchYet(true, true, false, seen), "3:30 watched, no match datacenter: hint");
            Check(!Zones.NoMatchYet(true, true, true, seen), "a datacenter was seen: no hint");
            Check(!Zones.NoMatchYet(false, true, false, seen) && !Zones.NoMatchYet(true, false, false, seen), "not locked here, or no zones: no hint");

            // the review case: lock, play a match with the game in front (checks paused), alt-tab back 20 minutes later
            seen = TimeSpan.Zero;
            seen = Zones.AddObserved(seen, DateTime.MinValue, t0);                        // one check right after the lock
            seen = Zones.AddObserved(seen, t0, t0.AddMinutes(20));                       // the next one, 20 min later
            seen = Zones.AddObserved(seen, t0.AddMinutes(20), t0.AddMinutes(20.5));
            Check(seen == TimeSpan.FromSeconds(30) && !Zones.NoMatchYet(true, true, false, seen), "paused time doesn't count: " + seen);
            Check(Zones.AddObserved(TimeSpan.FromMinutes(1), t0, t0.AddSeconds(98)) == TimeSpan.FromSeconds(158), "a 90 s check interval (+ capture) counts");
            Check(Zones.AddObserved(TimeSpan.FromMinutes(1), t0, t0.AddSeconds(-5)) == TimeSpan.FromMinutes(1), "clock going back adds nothing");
            Check(!Zones.NoMatchYet(true, true, false, TimeSpan.FromMinutes(3)), "exactly 3 min: not yet");
        }

        static void RunZoneTests()
        {
            Run("ZonesLearnFromServerChecks", ZonesLearnFromServerChecks);
            Run("NoMatchCountsWatchedTimeOnly", NoMatchCountsWatchedTimeOnly);
            Run("MinimizedTitle", MinimizedTitle);
            Run("OverwatchRegionsHaveZones", OverwatchRegionsHaveZones);
            Run("ZonesRememberWhenSeen", ZonesRememberWhenSeen);
            Run("ZoneWarningText", ZoneWarningText);
            Run("CardZoneWarning", CardZoneWarning);
            Run("CardNoMatchYet", CardNoMatchYet);
            Run("OverwatchServiceHostsStayOpen", OverwatchServiceHostsStayOpen);
            Run("OverwatchVoiceChatIgnored", OverwatchVoiceChatIgnored);
        }
    }
}
