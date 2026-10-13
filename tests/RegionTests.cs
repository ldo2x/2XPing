using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    static partial class Tests
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            if (ok) return;
            failures++;
            Console.WriteLine("    FAIL: " + what);
        }

        static void Run(string name, Action test)
        {
            Console.WriteLine(name);
            try { test(); }
            catch (Exception ex) { failures++; Console.WriteLine("    FAIL: threw " + ex); }
        }

        static Game Find(string name)
        {
            var g = GameDb.All().First(x => x.Name == name);
            GameDb.Load(g);
            return g;
        }

        static RegionDef Def(Game g, string key)
        {
            return g.RegionMap.FirstOrDefault(d => d.Key == key);
        }

        // a game with one datacenter per region: { key, ping (null = not tested yet) }
        static List<RegionChoice> Synthetic(Dictionary<string, int> activity, params object[][] regions)
        {
            var g = new Game { Name = "Fake", RegionMap = new List<RegionDef>() };
            foreach (var r in regions)
            {
                string key = (string)r[0];
                var region = new Region { Id = key.ToLower(), NameEn = key };
                if (r[1] != null) region.Direct = new PingResult { Avg = Convert.ToDouble(r[1]) };
                g.Regions.Add(region);
                g.RegionMap.Add(new RegionDef { Key = key, En = key, Ar = key, Ids = new[] { key.ToLower() } });
            }
            return RegionGroups.Build(g, activity, m => m * 10, DirectLatency);
        }

        static double DirectLatency(Region r)
        {
            return r.Direct != null && r.Direct.Ok ? r.Direct.Avg : double.MaxValue;
        }

        static Dictionary<string, int> Queue(params object[] kv)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1];
            return d;
        }

        static void OverwatchRegionsCoverEveryDatacenterOnce()
        {
            var g = Find("Overwatch 2");
            Check(g.Regions.Count > 0, "Overwatch datacenters loaded");
            Check(g.RegionMap != null, "Overwatch has a RegionMap");
            foreach (var r in g.Regions)
            {
                int n = g.RegionMap.Count(d => d.Ids.Contains(r.Id));
                Check(n == 1, r.Id + " is in " + n + " regions, expected 1");
            }
            var na = Def(g, "na");
            Check(na != null && na.Ids.OrderBy(x => x).SequenceEqual(new[] { "GUE4", "LAX1", "ORD1" }), "North America = GUE4, ORD1, LAX1");
            foreach (var d in g.RegionMap)
                foreach (var id in d.Ids)
                    Check(g.Regions.Any(r => r.Id == id), d.Key + " names unknown datacenter " + id);
        }

        static void DeadlockRegionsSkipRelayOnly()
        {
            var g = Find("Deadlock");
            Check(g.RegionMap != null && g.RegionMap.Count > 0, "Deadlock has a RegionMap after load");
            foreach (var d in g.RegionMap)
            {
                Check(!d.Ids.Contains("dxb") && !d.Ids.Contains("dfjr") && !d.Ids.Contains("bom2"), d.Key + " contains a relay-only datacenter");
                foreach (var id in d.Ids)
                    Check(g.Regions.Any(r => r.Id == id && g.Hosts(r)), d.Key + " has " + id + " which is not a hosting datacenter");
            }
            var eu = Def(g, "Europe");
            Check(eu != null && eu.Ids.Contains("fra") && eu.Ids.Contains("par"), "Europe contains fra and par");
            Check(!g.Regions.Any(r => r.Id == "jnb") || eu.Ids.Contains("jnb"), "Johannesburg is in Europe");
            var na = Def(g, "Row");
            Check(na != null && na.En == "North America", "Row is shown as North America");
        }

        static void DbdRegionsMatchSpec()
        {
            var g = Find("Dead by Daylight");
            var eu = Def(g, "eu");
            Check(eu != null && eu.Ids.OrderBy(x => x).SequenceEqual(new[] { "eu-central-1", "eu-west-1", "eu-west-2" }), "Europe members");
            var na = Def(g, "na");
            Check(na != null && na.Ids.OrderBy(x => x).SequenceEqual(new[] { "ca-central-1", "us-east-1", "us-east-2", "us-west-1", "us-west-2" }), "North America members");
            foreach (var r in g.Regions)
            {
                int n = g.RegionMap.Count(d => d.Ids.Contains(r.Id));
                Check(n == 1, r.Id + " is in " + n + " regions, expected 1");
            }
        }

        static void FeaturedPicksBestPingAndFastestQueue()
        {
            var all = Synthetic(Queue("A", 120, "B", 200, "C", 15), new object[] { "A", 78 }, new object[] { "B", 160 }, new object[] { "C", 110 });
            var f = RegionGroups.Featured(all);
            Check(f.Count == 3, "three tiles, got " + f.Count);
            Check(f[0].Def.Key == "A" && f[0].Tag == "ping", "first is A tagged ping");
            // both wait about 1-3 minutes: B has more players, not a shorter wait
            Check(f[1].Def.Key == "B" && f[1].Tag == "players", "second is B tagged players, got " + f[1].Tag);
            Check(f[2].Def.Key == "C" && f[2].Tag == null, "third is C with no tag");
            Check(f[0].Selected && !f[1].Selected && !f[2].Selected, "only A selected");
            Check(f[1].Matches == 200 && f[1].Players == 2000, "matches and players filled in");
        }

        static void FeaturedShortestWaitOnlyWhenShorter()
        {
            // A waits 3-6 minutes (20 matches), B 1-3 (200): B's wait really is shorter
            var f = RegionGroups.Featured(Synthetic(Queue("A", 20, "B", 200), new object[] { "A", 78 }, new object[] { "B", 160 }));
            Check(f[0].Tag == "ping" && f[1].Def.Key == "B" && f[1].Tag == "queue", "B tagged queue, got " + f[1].Tag);
            Check(Stats.WaitBucket(200) < Stats.WaitBucket(20) && Stats.WaitBucket(0) == 4, "buckets in order");
        }

        static void FeaturedCollapsesWhenOneWinsBoth()
        {
            var f = RegionGroups.Featured(Synthetic(Queue("A", 200, "B", 120), new object[] { "A", 78 }, new object[] { "B", 160 }));
            Check(f.Count == 2, "two tiles, got " + f.Count);
            Check(f[0].Def.Key == "A" && f[0].Tag == "best", "A tagged best");
            Check(f[1].Def.Key == "B" && f[1].Tag == null, "B has no tag");
        }

        static void FeaturedWithoutQueueData()
        {
            var f = RegionGroups.Featured(Synthetic(new Dictionary<string, int>(), new object[] { "A", 78 }, new object[] { "B", 160 }, new object[] { "C", 110 }));
            Check(f.Count == 3, "three tiles");
            Check(f[0].Tag == "ping", "first tagged ping");
            Check(f.All(c => c.Tag != "queue" && c.Tag != "best"), "no queue or best tag");
            Check(f.Skip(1).All(c => c.Tag == null), "the others have no tag");
            Check(f.All(c => c.Matches == -1 && c.Players == -1), "no matches or players");
            Check(f[1].Def.Key == "C" && f[2].Def.Key == "B", "sorted by ping");
        }

        static void UnknownPingSortsLast()
        {
            var f = RegionGroups.Featured(Synthetic(new Dictionary<string, int>(), new object[] { "A", null }, new object[] { "B", 90 }));
            Check(f[0].Def.Key == "B", "B (90) first");
            Check(f[1].Def.Key == "A" && f[1].Ping == double.MaxValue, "A (unknown) last");
        }

        static void RegionsWithoutHostingDatacentersAreDropped()
        {
            var g = new Game { Name = "Fake", HostPops = new HashSet<string> { "a" }, RegionMap = new List<RegionDef>() };
            g.Regions.Add(new Region { Id = "a" });
            g.Regions.Add(new Region { Id = "relay" });
            g.RegionMap.Add(new RegionDef { Key = "A", Ids = new[] { "a" } });
            g.RegionMap.Add(new RegionDef { Key = "R", Ids = new[] { "relay" } });
            g.RegionMap.Add(new RegionDef { Key = "Z", Ids = new[] { "missing" } });
            var all = RegionGroups.Build(g, new Dictionary<string, int>(), m => m, DirectLatency);
            Check(all.Count == 1 && all[0].Def.Key == "A", "only the region with a hosting datacenter is built");
        }

        static void PingUsesTheGivenLatencyAndTheBestMember()
        {
            var g = new Game { Name = "Fake", RegionMap = new List<RegionDef>() };
            g.Regions.Add(new Region { Id = "a1", Direct = new PingResult { Avg = 100 } });
            g.Regions.Add(new Region { Id = "a2", Direct = new PingResult { Avg = 140 } });
            g.RegionMap.Add(new RegionDef { Key = "A", Ids = new[] { "a1", "a2" } });
            // a2 is faster through a relay, like MainForm.BestLatency would report
            var all = RegionGroups.Build(g, null, null, r => r.Id == "a2" ? 60 : DirectLatency(r));
            Check(all.Count == 1 && all[0].Ping == 60, "ping is the best member's latency, relay included");
        }

        static void BusyWords()
        {
            Lang.Ar = false;
            Check(RegionGroups.Busy(10000) == "lots of players", "10000 = lots");
            Check(RegionGroups.Busy(9999) == "some players", "9999 = some");
            Check(RegionGroups.Busy(2000) == "some players", "2000 = some");
            Check(RegionGroups.Busy(1999) == "few players", "1999 = few");
            Lang.Ar = true;
        }

        static List<RegionChoice> Choices(Game g)
        {
            return RegionGroups.Build(g, null, null, DirectLatency);
        }

        static void TwoRegionsLockExactlyTheirDatacenters()
        {
            var g = Find("Deadlock");
            var all = Choices(g);
            // the map has no Russia (no datacenter serves it), so Oceania stands in for the second region
            Check(Def(g, "Russia") == null, "Russia has no datacenter in the map");
            var eu = all.First(c => c.Def.Key == "Europe");
            var oc = all.First(c => c.Def.Key == "Oceania");
            var ids = RegionGroups.PickIds(g, new[] { eu, oc });
            var expected = eu.Members.Concat(oc.Members).Select(r => r.Id).ToList();
            Check(ids.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)), "ids are the union of both regions' hosting datacenters");
            Check(ids.Count == ids.Distinct().Count(), "no duplicate ids");
            Check(ids.Contains("fra") && ids.Contains("syd"), "has fra and syd");
            Check(!ids.Contains("iad") && !ids.Contains("lax"), "no North America datacenter");
            Check(!ids.Contains("dxb"), "no relay-only datacenter");
        }

        static void PickIdsSkipsNonHostingAndDuplicates()
        {
            var g = new Game { Name = "Fake", HostPops = new HashSet<string> { "a", "b" } };
            var a = new Region { Id = "a" };
            var b = new Region { Id = "b" };
            var relay = new Region { Id = "relay" };
            var one = new RegionChoice { Def = RegionDef.Of("One", "One", "One", "a", "relay"), Members = new List<Region> { a, relay } };
            var two = new RegionChoice { Def = RegionDef.Of("Two", "Two", "Two", "a", "b"), Members = new List<Region> { a, b } };
            var ids = RegionGroups.PickIds(g, new[] { one, two });
            Check(ids.SequenceEqual(new[] { "a", "b" }), "a once, then b; relay left out");
            Check(RegionGroups.PickIds(g, new RegionChoice[0]).Count == 0, "nothing chosen = nothing picked");
        }

        // A region picked under "Other regions" (not one of the featured tiles) is named on the button and locked,
        // whether the other regions are shown or folded away: the card decides from the whole list.
        static void ChosenOtherRegionIsNamedAndLocked()
        {
            var g = new Game { Name = "Fake", RegionMap = new List<RegionDef>() };
            foreach (var k in new[] { "Aa", "Bb", "Cc", "Dd", "Ee" })
            {
                g.Regions.Add(new Region { Id = k.ToLower(), NameEn = k, Direct = new PingResult { Avg = 10 * (g.Regions.Count + 1) } });
                g.RegionMap.Add(RegionDef.Of(k, k, k, k.ToLower()));
            }
            var all = RegionGroups.Build(g, null, null, DirectLatency);
            var featured = RegionGroups.Featured(all);
            var every = RegionGroups.FeaturedFirst(all, featured);
            Check(featured.Count == 3 && !featured.Any(c => c.Def.Key == "Ee"), "Ee is not featured");
            Check(every.Count == 5 && every.Take(3).SequenceEqual(featured), "featured first, then the rest");
            var ee = every.First(c => c.Def.Key == "Ee");

            // the best (featured) one stays on and Ee is added
            ee.Selected = true;
            var input = new CardInput { Game = "Fake", Loaded = true, Tested = true, CanLock = true, Choices = every };
            var v = CardLogic.Decide(input);
            Check(v.Kind == CardKind.Choose && v.Button != null && v.Button.Contains("Aa") && v.Button.Contains("Ee"), "button names both: " + v.Button);
            Check(v.Sub != null && v.Sub.Contains("10") && v.Sub.Contains("50"), "ping range covers Ee: " + v.Sub);
            var ids = RegionGroups.PickIds(g, RegionGroups.Chosen(every));
            Check(ids.Count == 2 && ids.Contains("aa") && ids.Contains("ee"), "locks aa and ee: " + string.Join(",", ids.ToArray()));

            // only Ee: still a button, and only ee is locked
            featured[0].Selected = false;
            v = CardLogic.Decide(input);
            Check(v.Kind == CardKind.Choose && v.Button != null && v.Button.Contains("Ee") && !v.Button.Contains("Aa"), "button names Ee alone: " + v.Button);
            ids = RegionGroups.PickIds(g, RegionGroups.Chosen(every));
            Check(ids.Count == 1 && ids[0] == "ee", "locks only ee: " + string.Join(",", ids.ToArray()));
        }

        static int Main()
        {
            Run("OverwatchRegionsCoverEveryDatacenterOnce", OverwatchRegionsCoverEveryDatacenterOnce);
            Run("DeadlockRegionsSkipRelayOnly", DeadlockRegionsSkipRelayOnly);
            Run("DbdRegionsMatchSpec", DbdRegionsMatchSpec);
            Run("FeaturedPicksBestPingAndFastestQueue", FeaturedPicksBestPingAndFastestQueue);
            Run("FeaturedShortestWaitOnlyWhenShorter", FeaturedShortestWaitOnlyWhenShorter);
            Run("FeaturedCollapsesWhenOneWinsBoth", FeaturedCollapsesWhenOneWinsBoth);
            Run("FeaturedWithoutQueueData", FeaturedWithoutQueueData);
            Run("UnknownPingSortsLast", UnknownPingSortsLast);
            Run("RegionsWithoutHostingDatacentersAreDropped", RegionsWithoutHostingDatacentersAreDropped);
            Run("PingUsesTheGivenLatencyAndTheBestMember", PingUsesTheGivenLatencyAndTheBestMember);
            Run("BusyWords", BusyWords);
            Run("TwoRegionsLockExactlyTheirDatacenters", TwoRegionsLockExactlyTheirDatacenters);
            Run("PickIdsSkipsNonHostingAndDuplicates", PickIdsSkipsNonHostingAndDuplicates);
            Run("ChosenOtherRegionIsNamedAndLocked", ChosenOtherRegionIsNamedAndLocked);
            RunCardTests();
            RunDeadlockTests();
            RunDetectionTests();
            RunLockTestTests();
            RunZoneTests();
            RunSafetyTests();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
