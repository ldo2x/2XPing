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

        static void ZonesListOnlyGrows()
        {
            Check(Zones.Add(null, "am") == "am", "first zone");
            Check(Zones.Add("am", "as") == "am,as", "second zone");
            Check(Zones.Add("am,as", "am") == "am,as", "no duplicate");
            Check(Zones.Add("am", null) == "am", "a datacenter without a zone adds nothing");
            Check(Zones.Add("am,junk", "xx") == "am", "unknown entries dropped");
            var p = Zones.Parse(" eu , as,,eu");
            Check(p.Count == 2 && p[0] == "eu" && p[1] == "as", "parse: " + string.Join("|", p.ToArray()));
            Check(Zones.Parse(null).Count == 0 && Zones.Parse("").Count == 0, "nothing saved");
        }

        static void ZoneWarningText()
        {
            Lang.Ar = true;
            var sg = ZoneChoice("sg", "Singapore", "سنغافورة", Zones.Asia, true);
            var am = new List<string> { Zones.Americas };
            string w = Zones.Warning(new List<RegionChoice> { sg }, am);
            Check(w == "سنغافورة في منطقة آسيا، وإنت تلعب على أمريكا. ما بتلقى مباراة إلا لو كانت لعبتك على منطقة آسيا.", "Arabic: " + w);
            Lang.Ar = false;
            w = Zones.Warning(new List<RegionChoice> { sg }, am);
            Check(w == "Singapore is in Asia, but you play on Americas. You'll only find a match if your game is set to Asia.", "English: " + w);

            // two regions, two zones, two zones seen
            var eu = ZoneChoice("eu", "Europe", "أوروبا", Zones.Europe, true);
            var ea = ZoneChoice("ea", "East Asia", "شرق آسيا", Zones.Asia, true);
            w = Zones.Warning(new List<RegionChoice> { ea, sg }, am);
            Check(w == "East Asia and Singapore are in Asia, but you play on Americas. You'll only find a match if your game is set to Asia.", "English two in one zone: " + w);
            Lang.Ar = true;
            var na = ZoneChoice("na", "North America", "أمريكا الشمالية", Zones.Americas, true);
            w = Zones.Warning(new List<RegionChoice> { na, sg }, new List<string> { Zones.Europe });
            Check(w == "أمريكا الشمالية في منطقة أمريكا وسنغافورة في منطقة آسيا، وإنت تلعب على أوروبا. ما بتلقى مباراة إلا لو كانت لعبتك على منطقة أمريكا أو آسيا.", "Arabic two zones: " + w);
            w = Zones.Warning(new List<RegionChoice> { sg }, new List<string> { Zones.Americas, Zones.Europe });
            Check(w == "سنغافورة في منطقة آسيا، وإنت تلعب على أمريكا وأوروبا. ما بتلقى مباراة إلا لو كانت لعبتك على منطقة آسيا.", "Arabic two seen: " + w);

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
            Check(v.Kind == CardKind.Locked && v.Sub == "افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول. ما لقيت مباراة؟ يمكن المنطقة اللي اخترتها برا منطقة لعبتك.", "Arabic: " + v.Sub);
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

        // The block list built like MainForm.LockServers, with only Singapore picked.
        static List<string> OverwatchBlock(Game g, params string[] picked)
        {
            var allow = IpLists.Resolve(g.LockAllow ?? new string[0]);
            var keep = g.Regions.Where(r => picked.Contains(r.Id)).SelectMany(r => r.Cidrs);
            return IpLists.Subtract(IpLists.Resolve(g.LockSpace), keep.Concat(allow));
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
            var server = MainForm.ServerHits(g, hits);
            Check(server.Count == 1 && server[0].Ip == "137.221.69.103", "only the match server is left: " + string.Join(",", server.Select(h => h.Ip).ToArray()));
            Check(MainForm.ServerHits(g, new List<Detector.Hit> { hits[0] }).Count == 0, "voice chat alone = not in a match (no unknown_server)");
            var dbd = Find("Dead by Daylight");
            Check(!dbd.Ignored("85.236.97.1"), "other games ignore nothing");
        }

        static void RunZoneTests()
        {
            Run("OverwatchRegionsHaveZones", OverwatchRegionsHaveZones);
            Run("ZonesListOnlyGrows", ZonesListOnlyGrows);
            Run("ZoneWarningText", ZoneWarningText);
            Run("CardZoneWarning", CardZoneWarning);
            Run("CardNoMatchYet", CardNoMatchYet);
            Run("OverwatchServiceHostsStayOpen", OverwatchServiceHostsStayOpen);
            Run("OverwatchVoiceChatIgnored", OverwatchVoiceChatIgnored);
        }
    }
}
