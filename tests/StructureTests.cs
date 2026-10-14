using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // Task 7: the units taken out of MainForm (Fmt, Routing, ...).
    static partial class Tests
    {
        static Region Reg(string id, double direct)
        {
            return new Region { Id = id, NameEn = id, Direct = direct < 0 ? new PingResult() : new PingResult { Avg = direct } };
        }

        static void RoutingRelayMarginIsThreeMs()
        {
            var relays = new List<Relay> { new Relay { Name = "fra" } };
            var r = Reg("eu", 50);
            r.ViaRelay["fra"] = 47.5; // only 2.5 ms better: stay direct
            Check(Routing.AutoRelay(r, relays) == null, "2.5 ms better stays direct");
            r.ViaRelay["fra"] = 46.9; // more than 3 ms better
            Check(Routing.AutoRelay(r, relays) == relays[0], "3.1 ms better takes the relay");
            Check(Routing.BestLatency(r) == 46.9, "best latency is the relay's");
            r.ViaRelay["nowhere"] = 10; // a relay the player no longer has
            Check(Routing.AutoRelay(r, relays) == null, "unknown relay name -> null");
        }

        static void RoutingDirectDownTakesRelay()
        {
            var relays = new List<Relay> { new Relay { Name = "fra" } };
            var r = Reg("eu", -1);
            Check(Routing.AutoRelay(r, relays) == null, "no relay pings -> null");
            Check(Routing.BestLatency(r) == double.MaxValue, "nothing answered = MaxValue");
            r.ViaRelay["fra"] = 80;
            Check(Routing.AutoRelay(r, relays) == relays[0], "direct down -> relay");
            Check(Routing.BestLatency(r) == 80, "best latency 80");
            var n = new Region { Id = "x", NameEn = "x" };
            Check(Routing.BestLatency(n) == double.MaxValue && Routing.AutoRelay(n, relays) == null, "never tested");
        }

        static void RoutingBestAndSort()
        {
            var a = Reg("a", 70); var b = Reg("b", 30); var c = Reg("c", -1); var d = Reg("d", 20);
            Check(Routing.Best(new[] { a, b, c }) == b, "best is lowest answered");
            Check(Routing.Best(new[] { c }) == null, "nothing answered -> null");
            Check(Routing.Best(new Region[0]) == null, "empty -> null");
            var sorted = Routing.SortForList(new[] { a, b, c, d }, r => r == d);
            Check(string.Join(",", sorted.Select(r => r.Id).ToArray()) == "b,a,c,d", "locked-out last, then by ping: " +
                  string.Join(",", sorted.Select(r => r.Id).ToArray()));
        }

        static void FmtShortNameAndPing()
        {
            Check(Fmt.ShortName("Frankfurt  [fra]") == "Frankfurt", "strips [x]");
            Check(Fmt.ShortName("Dubai  (UAE)") == "Dubai", "strips (x)");
            Check(Fmt.ShortName("Stockholm") == "Stockholm", "plain name unchanged");
            Check(Fmt.ShortName(new Region { NameEn = "Vienna  [vie]" }) == "Vienna", "region overload");
            Lang.Ar = false;
            Check(Fmt.Ms(-1) == "no reply", "no reply");
            Check(Fmt.Ms(73.6) == "‎74 ms‎", "rounded with LRM marks");
            Lang.Ar = true;
            Check(Fmt.Ms(-1) == "ما يرد", "Arabic no reply");
            Check(Fmt.PingColor(double.MaxValue) == Theme.Dim, "unknown = dim");
            Check(Fmt.PingColor(-1) == Theme.Bad, "no reply = bad");
            Check(Fmt.PingColor(59) == Theme.Good && Fmt.PingColor(60) == Theme.Warn && Fmt.PingColor(119) == Theme.Warn
                  && Fmt.PingColor(120) == Theme.Bad, "thresholds 60/120");
        }

        static List<RegionChoice> Tiles(params object[] keyPing)
        {
            var l = new List<RegionChoice>();
            for (int i = 0; i < keyPing.Length; i += 2)
                l.Add(new RegionChoice { Def = RegionDef.Of((string)keyPing[i], (string)keyPing[i], (string)keyPing[i]), Ping = (double)keyPing[i + 1] });
            return l;
        }

        static string Picked(ChoiceSet c) { return string.Join(",", c.All.Where(x => x.Selected).Select(x => x.Def.Key).ToArray()); }

        static void ChoiceSetKeepsThePlayersSelection()
        {
            var g = new Game { Name = "Fake" };
            var c = new ChoiceSet();
            Check(c.Rebuild(g, Tiles("eu", 40.0, "me", 70.0, "as", 120.0)), "first build");
            Check(Picked(c) == "eu", "best starts selected: " + Picked(c));
            Check(c.Featured.Count > 0 && c.Every.Count == 3 && c.Every[0].Def.Key == "eu", "featured first");
            // the player picks "me"; new pings rebuild the tiles but keep that pick
            foreach (var x in c.All) x.Selected = x.Def.Key == "me";
            c.UserPicked = true;
            Check(c.Rebuild(g, Tiles("eu", 41.0, "me", 69.0, "as", 120.0)), "new pings rebuild");
            Check(Picked(c) == "me", "selection survives: " + Picked(c));
            // the picked region is gone: the best one is selected again
            foreach (var x in c.All) x.Selected = x.Def.Key == "as";
            Check(c.Rebuild(g, Tiles("eu", 41.0, "me", 69.0)), "region list changed");
            Check(Picked(c) == "eu", "gone -> best: " + Picked(c));
            // without UserPicked the new list's own best wins
            c.UserPicked = false;
            foreach (var x in c.All) x.Selected = x.Def.Key == "eu";
            c.Rebuild(g, Tiles("eu", 90.0, "me", 69.0));
            Check(Picked(c) == "me", "best by ping after rebuild: " + Picked(c));
            // another game never keeps the old game's pick
            c.UserPicked = true;
            c.Rebuild(new Game { Name = "Other" }, Tiles("eu", 90.0, "me", 69.0, "us", 10.0));
            Check(Picked(c) == "us", "another game starts at its best: " + Picked(c));
        }

        static void ChoiceSetSignatureStopsPointlessRebuilds()
        {
            var g = new Game { Name = "Fake" };
            var c = new ChoiceSet();
            c.Rebuild(g, Tiles("eu", 40.2, "me", 70.0));
            var before = c.All;
            Check(!c.Rebuild(g, Tiles("eu", 39.9, "me", 70.0)), "same rounded pings: no rebuild");
            Check(c.All == before, "tiles kept");
            Check(c.Rebuild(g, Tiles("eu", 39.4, "me", 70.0)), "a different rounded ping rebuilds");
            Check(c.Rebuild(new Game { Name = "Other" }, Tiles("eu", 39.4, "me", 70.0)), "another game rebuilds");
            c.Forget();
            Check(c.Game == null && c.Rebuild(g, Tiles("eu", 39.4, "me", 70.0)), "Forget rebuilds");
            string sig = ChoiceSet.Signature(Tiles("x", double.MaxValue));
            Check(sig == "x:-::-1:-1", "unknown ping in the signature: " + sig);
        }

        static CardFacts Facts()
        {
            return new CardFacts { Loaded = true, PlayersOf = m => m * 10, SeenZones = () => null, RoutePreview = ch => null };
        }

        static void CardInputTestingRule()
        {
            var g = new Game { Name = "G", Provider = Provider.Valve };
            var other = new Game { Name = "O" };
            var none = new List<RegionChoice>();
            var f = Facts();
            Check(!CardInputBuilder.Build(g, none, f).Testing, "nothing running");
            f.TestGame = g;
            Check(CardInputBuilder.Build(g, none, f).Testing, "this game's test");
            f.TestGame = other;
            Check(!CardInputBuilder.Build(g, none, f).Testing, "another game's test, no auto test, not next");
            f.TestNext = g;
            Check(CardInputBuilder.Build(g, none, f).Testing, "queued next");
            f.TestNext = null; f.AutoTest = true;
            Check(CardInputBuilder.Build(g, none, f).Testing, "auto test queues it");
            f.Tested = true;
            Check(!CardInputBuilder.Build(g, none, f).Testing, "already tested: not again");
            f.NotAutoTested = true;
            Check(!CardInputBuilder.Build(g, none, f).OldPings, "tested: pings are fresh");
            f.Tested = false;
            Check(CardInputBuilder.Build(g, none, f).OldPings, "skipped auto test: old pings");
        }

        static void CardInputLockAndMatch()
        {
            var g = new Game { Name = "G", Provider = Provider.Valve };
            var other = new Game { Name = "O", Provider = Provider.Valve };
            var none = new List<RegionChoice>();
            var f = Facts();
            f.LockGame = other; f.LockNames = "Europe"; f.NoMatchLong = true;
            var i = CardInputBuilder.Build(g, none, f);
            Check(i.LockedNames == null && !i.NoMatchYet, "another game's lock is not this card's");
            f.LockGame = g;
            i = CardInputBuilder.Build(g, none, f);
            Check(i.LockedNames == "Europe" && i.NoMatchYet, "this game's lock");
            f.LockingGame = g; f.LockingNames = "Asia";
            i = CardInputBuilder.Build(g, none, f);
            Check(i.Locking && i.LockingNames == "Asia", "locking");
            f.LockingGame = other;
            i = CardInputBuilder.Build(g, none, f);
            Check(!i.Locking && i.LockingNames == null, "another game's lock being applied");
            // the Deadlock hint: none with the log on; a link in the simple layout; the steps otherwise
            var dl = new Game { Name = "Deadlock", Icon = "deadlock", AppId = "1422450", Provider = Provider.Valve };
            f.Simple = true;
            Check(CardInputBuilder.Build(dl, none, f).Hint == ExactHint.Link, "simple: link");
            f.DevMode = true;
            Check(CardInputBuilder.Build(dl, none, f).Hint == ExactHint.Shown, "developer: shown");
            f.GameLogOn = true;
            Check(CardInputBuilder.Build(dl, none, f).Hint == ExactHint.None, "log on: none");
            Check(CardInputBuilder.Build(g, none, f).Hint == ExactHint.None, "not Deadlock: none");
            // Russia's players from the activity numbers
            f.Activity = new Dictionary<string, int> { { DeadlockData.Russia, 7 } };
            i = CardInputBuilder.Build(dl, none, f);
            Check(i.RussiaNote && i.RussiaPlayers == 70, "Russia players " + i.RussiaPlayers);
            // the match the automatic check saw is passed through
            f.MatchRegion = "Europe"; f.MatchOnPicked = true; f.CurrentServer = "Vienna"; f.CurrentMs = 41;
            i = CardInputBuilder.Build(g, none, f);
            Check(i.MatchRegion == "Europe" && i.MatchOnPicked && i.CurrentServer == "Vienna" && i.CurrentMs == 41, "match facts");
        }

        static void CardInputZoneWarningOnlyForLockableGamesWithZones()
        {
            var tiles = new List<RegionChoice> { new RegionChoice { Def = RegionDef.Of("sg", "Singapore", "Singapore").In(Zones.Asia), Selected = true } };
            var f = Facts();
            int asked = 0;
            f.SeenZones = () => { asked++; return new List<string> { Zones.Europe }; };
            var ow = new Game { Name = "OW", LockSpace = new[] { "x" }, RegionMap = new List<RegionDef> { tiles[0].Def } };
            Check(CardInputBuilder.Build(ow, tiles, f).ZoneWarning != null && asked == 1, "lockable with zones: warned");
            var noZones = new Game { Name = "DbD", LockSpace = new[] { "x" }, RegionMap = new List<RegionDef> { RegionDef.Of("a", "A", "A") } };
            Check(CardInputBuilder.Build(noZones, tiles, f).ZoneWarning == null && asked == 1, "no zones: not asked");
            var cantLock = new Game { Name = "AB", RegionMap = new List<RegionDef> { tiles[0].Def } };
            Check(CardInputBuilder.Build(cantLock, tiles, f).ZoneWarning == null && asked == 1, "can't lock: not asked");
        }

        static void CardInputBestServerForUnlockableGames()
        {
            var g = new Game { Name = "AB" };
            var near = Reg("near", 30); near.NameEn = "Bahrain  [bah]";
            var found = Reg("found", 10); found.Detected = true;
            g.Regions.Add(Reg("far", 90)); g.Regions.Add(near); g.Regions.Add(found);
            Lang.Ar = false;
            var i = CardInputBuilder.Build(g, new List<RegionChoice>(), Facts());
            Check(i.BestServer == "Bahrain" && i.BestMs == 30, "best own server, not the detected one: " + i.BestServer + " " + i.BestMs);
            var v = new Game { Name = "V", Provider = Provider.Valve };
            v.Regions.Add(near);
            Check(CardInputBuilder.Build(v, new List<RegionChoice>(), Facts()).BestServer == null, "lockable games name no best server");
            Lang.Ar = true;
        }

        static Region Dc(string id, string cidr, bool picked)
        {
            return new Region { Id = id, NameEn = id, Picked = picked, Cidrs = new List<string> { cidr } };
        }

        static void LockPlannerValveKeepsRelayOnlyPopsOpen()
        {
            var g = new Game { Name = "V", Provider = Provider.Valve, HostPops = new HashSet<string> { "fra", "sto", "dxb" } };
            var fra = Dc("fra", "10.0.1.0/24", true);
            var sto = Dc("sto", "10.0.2.0/24", false);
            var dxb = Dc("dxb", "10.0.3.0/24", false);
            var relay = Dc("relay", "10.0.4.0/24", false);       // relay only: the game enters through it
            var closedRelay = Dc("closed", "10.0.5.0/24", false); // a relay the strict lock closes
            var found = Dc("found", "10.0.6.0/24", false); found.Detected = true;
            g.Regions.AddRange(new[] { fra, sto, dxb, relay, closedRelay, found });
            g.HostPops.Add("found");
            var closed = new HashSet<string> { "closed" };
            Func<IEnumerable<string>, List<string>> resolve = specs => specs.ToList();
            var info = LockPlanner.Plan(g, new List<Region> { fra }, null, LockPlanner.ClosedCidrs(g, closed), resolve);
            string block = string.Join(" ", info.Block.ToArray());
            Check(block == "10.0.2.0/24 10.0.3.0/24 10.0.5.0/24", "other hosts + the closed relay: " + block);
            Check(info.Mode == "valve-all" && info.Program == null && info.Protocol == null && info.Ports == null, "whole PC, any protocol");
            Check(info.Active && info.Picked.SequenceEqual(new[] { "fra" }), "picked");
            // a picked datacenter's range is never blocked, even when a closed relay shares it
            var shared = LockPlanner.Plan(g, new List<Region> { fra }, null, new List<string> { "10.0.1.0/24" }, resolve);
            Check(!shared.Block.Contains("10.0.1.0/24"), "picked range stays open");
        }

        static void LockPlannerLockSpaceMinusPickedMinusAllow()
        {
            var g = new Game { Name = "OW", LockSpace = new[] { "space" }, LockAllow = new[] { "allow" }, LockPorts = "1-3723,3725-65535" };
            var pick = Dc("sg", "10.1.0.0/16", true);
            g.Regions.Add(pick);
            g.Regions.Add(Dc("us", "10.2.0.0/16", false));
            var asked = new List<string>();
            Func<IEnumerable<string>, List<string>> resolve = specs =>
            {
                var l = specs.ToList(); asked.AddRange(l);
                if (l.Contains("space")) return new List<string> { "10.0.0.0/8" };
                if (l.Contains("allow")) return new List<string> { "10.3.0.0/16" };
                return new List<string>();
            };
            var info = LockPlanner.Plan(g, new List<Region> { pick }, @"C:\Games\Overwatch.exe", new List<string>(), resolve);
            Check(asked.Contains("space") && asked.Contains("allow"), "both lists resolved");
            Check(!LockTest.Blocks(info.Block, "10.1.2.3"), "picked datacenter open");
            Check(!LockTest.Blocks(info.Block, "10.3.2.3"), "service hosts (allow) never blocked");
            Check(LockTest.Blocks(info.Block, "10.2.2.3") && LockTest.Blocks(info.Block, "10.200.0.1"), "the rest of the space blocked");
            Check(info.Allow.SequenceEqual(new[] { "10.3.0.0/16" }), "allow kept for the lock test");
            Check(info.Mode == "game-udp" && info.Protocol == "UDP" && info.Program == @"C:\Games\Overwatch.exe" && info.Ports == "1-3723,3725-65535",
                  "the game's own UDP on its ports");
            // without the exe (never happens for LockSpace games: LockServers asks for it) no ports either
            var noExe = LockPlanner.Plan(g, new List<Region> { pick }, null, new List<string>(), resolve);
            Check(noExe.Protocol == null && noExe.Ports == null, "no exe: no protocol, no ports");
        }

        static void LockPlannerScopeAndCardNames()
        {
            var info = new LockTest.LockInfo { Protocol = "UDP", Program = "ow.exe", Ports = "1-100" };
            Check(LockPlanner.Scope(info, 12) == "UDP, in+out, program ow.exe, remote ports 1-100, 12 ranges", LockPlanner.Scope(info, 12));
            Check(LockPlanner.Scope(new LockTest.LockInfo(), 3) == "any protocol, in+out, whole PC (every program), all ports, 3 ranges",
                  LockPlanner.Scope(new LockTest.LockInfo(), 3));
            Lang.Ar = false;
            var g = new Game { Name = "V", Provider = Provider.Valve };
            var a1 = Dc("a1", "1", true); var a2 = Dc("a2", "2", true); var b1 = Dc("b1  [b1]", "3", true);
            var eu = new RegionChoice { Def = RegionDef.Of("eu", "Europe", "أوروبا"), Members = new List<Region> { a1, a2 } };
            var me = new RegionChoice { Def = RegionDef.Of("me", "Middle East", "الشرق الأوسط"), Members = new List<Region> { b1 } };
            var tiles = new List<RegionChoice> { eu, me };
            Check(LockPlanner.CardNames(g, new List<Region> { a1, a2 }, tiles) == "Europe", "a whole region: its name");
            Check(LockPlanner.CardNames(g, new List<Region> { a1, a2, b1 }, tiles) == CardLogic.JoinNames(new List<string> { "Europe", "Middle East" }),
                  "two whole regions: " + LockPlanner.CardNames(g, new List<Region> { a1, a2, b1 }, tiles));
            Check(LockPlanner.CardNames(g, new List<Region> { a1, b1 }, tiles) == "a1, b1", "part of a region: the servers' short names: " +
                  LockPlanner.CardNames(g, new List<Region> { a1, b1 }, tiles));
            Lang.Ar = true;
        }

        static void MatchClassifierDeadlock()
        {
            var g = SavedDeadlock();
            string dxb = RelayIp(g, "dxb"), fra = RelayIp(g, "fra");
            // Dubai carries the game, Frankfurt is the backup router: through a relay, never "on Frankfurt"
            var m = MatchClassifier.Classify(g, Detector.Top(new[] { HitOf(dxb, 950), HitOf(fra, 50) }));
            Check(m.Kind == MatchKind.ViaRelay && m.Relay != null && m.Relay.Id == "dxb" && m.Datacenter == null && m.Found.Count == 0,
                  "relay-only hits -> ViaRelay at Dubai: " + m.Kind);
            // Frankfurt carries the match; picked -> on a picked server
            foreach (var r in g.Regions) r.Picked = r.Id == "fra";
            m = MatchClassifier.Classify(g, Detector.Top(new[] { HitOf(dxb, 100), HitOf(fra, 900) }));
            Check(m.Kind == MatchKind.OnDatacenter && m.Datacenter.Id == "fra" && m.AllPicked && m.Relay == null, "hosting dc + picked: " + m.Kind);
            foreach (var r in g.Regions) r.Picked = false;
            Check(!MatchClassifier.Classify(g, Detector.Top(new[] { HitOf(dxb, 100), HitOf(fra, 900) })).AllPicked, "not picked");
            // nothing captured: not in a match
            Check(MatchClassifier.Classify(g, new List<Detector.Hit>()).Kind == MatchKind.NoMatch, "no hits -> NoMatch");
            // traffic, but not to Valve
            m = MatchClassifier.Classify(g, Detector.Top(new[] { HitOf("8.8.8.8", 500) }));
            Check(m.Kind == MatchKind.OnUnknownServer && m.Datacenter == null && m.Relay == null, "no Valve address -> unknown server");
            // HostsOf names every hosting datacenter that carries the match, the busiest first; HostRegionOf is its first
            var hits = Detector.Top(new[] { HitOf(fra, 600), HitOf(dxb, 400) });
            Check(MatchClassifier.HostsOf(g, hits).Select(r => r.Id).SequenceEqual(new[] { "fra" }), "only the half-or-more address");
            Check(MatchClassifier.HostRegionOf(g, hits) == MatchClassifier.HostsOf(g, hits).FirstOrDefault(), "HostRegionOf = HostsOf first");
        }

        static void MatchClassifierOtherGames()
        {
            // Arena Breakout: no datacenter list, so any server is an unknown one (the city is looked up)
            var ab = new Game { Name = "Arena Breakout", Processes = new[] { "UAGame" } };
            var m = MatchClassifier.Classify(ab, new List<Detector.Hit> { HitOf("20.1.2.3", 300) });
            Check(m.Kind == MatchKind.OnUnknownServer, "Arena Breakout hits -> OnUnknownServer: " + m.Kind);
            // Overwatch: voice chat + Irvine (menu) are left out by ServerHits, so that is no match at all
            var ow = Find("Overwatch 2");
            var menu = new List<Detector.Hit>
            {
                new Detector.Hit { Ip = "85.236.97.1", Port = 12000, Packets = 900 },
                new Detector.Hit { Ip = "64.224.3.9", Port = 1119, Packets = 300 },
            };
            Check(MatchClassifier.Classify(ow, MatchClassifier.ServerHits(ow, menu)).Kind == MatchKind.NoMatch, "Overwatch voice + Irvine -> NoMatch");
            // a match server in Chicago (not Valve: no packet share needed)
            menu.Add(new Detector.Hit { Ip = "137.221.69.103", Port = 26503, Packets = 40 });
            m = MatchClassifier.Classify(ow, MatchClassifier.ServerHits(ow, menu));
            Check(m.Kind == MatchKind.OnDatacenter && m.Datacenter.Id == "ORD1", "Overwatch Chicago: " + m.Kind);
        }

        static void RunStructureTests()
        {
            Run("RoutingRelayMarginIsThreeMs", RoutingRelayMarginIsThreeMs);
            Run("RoutingDirectDownTakesRelay", RoutingDirectDownTakesRelay);
            Run("RoutingBestAndSort", RoutingBestAndSort);
            Run("FmtShortNameAndPing", FmtShortNameAndPing);
            Run("ChoiceSetKeepsThePlayersSelection", ChoiceSetKeepsThePlayersSelection);
            Run("ChoiceSetSignatureStopsPointlessRebuilds", ChoiceSetSignatureStopsPointlessRebuilds);
            Run("CardInputTestingRule", CardInputTestingRule);
            Run("CardInputLockAndMatch", CardInputLockAndMatch);
            Run("CardInputZoneWarningOnlyForLockableGamesWithZones", CardInputZoneWarningOnlyForLockableGamesWithZones);
            Run("CardInputBestServerForUnlockableGames", CardInputBestServerForUnlockableGames);
            Run("LockPlannerValveKeepsRelayOnlyPopsOpen", LockPlannerValveKeepsRelayOnlyPopsOpen);
            Run("LockPlannerLockSpaceMinusPickedMinusAllow", LockPlannerLockSpaceMinusPickedMinusAllow);
            Run("LockPlannerScopeAndCardNames", LockPlannerScopeAndCardNames);
            Run("MatchClassifierDeadlock", MatchClassifierDeadlock);
            Run("MatchClassifierOtherGames", MatchClassifierOtherGames);
        }
    }
}
