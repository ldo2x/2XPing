using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

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

        static void LossTrackerReportsFifteenPercent()
        {
            var t = new LossTracker();
            int lost = 0;
            bool reported = false;
            for (int i = 0; i < 59; i++) reported |= t.Add("eu", i < 20 ? -1 : 50, out lost);
            Check(!reported, "fewer than 60 samples: never, even with 20 lost");
            // 8 of 60 lost (13%): no report
            t = new LossTracker();
            for (int i = 0; i < 60; i++) reported = t.Add("eu", i < 8 ? -1 : 50, out lost);
            Check(!reported, "8/60 = no report");
            // 9 of 60 lost (15%): report
            t = new LossTracker();
            for (int i = 0; i < 60; i++) reported = t.Add("eu", i < 9 ? -1 : 50, out lost);
            Check(reported && lost == 9, "9/60 (15%) = report");
            // the window slides: the oldest lost ping leaves, 8 lost remain
            reported = t.Add("eu", 50, out lost);
            Check(!reported && lost == 8, "sliding window: " + lost);
            // a new label starts over
            reported = t.Add("asia", -1, out lost);
            Check(!reported && lost == 0, "label change resets");
        }

        static void HeldNoticesKeepFourWithoutRepeats()
        {
            var h = new HeldNotices();
            for (int i = 1; i <= 6; i++) h.Hold("n" + i, ToastKind.Warn, false);
            var all = h.TakeAll();
            Check(all.Count == HeldNotices.Max && all[0].Key == "n3" && all[3].Key == "n6", "max 4, oldest dropped first");
            Check(h.Count == 0 && h.TakeAll().Count == 0, "TakeAll empties it");
            h.Hold("a", ToastKind.Warn, false);
            h.Hold("b", ToastKind.Warn, false);
            h.Hold("a", ToastKind.Bad, false);
            all = h.TakeAll();
            Check(all.Count == 2 && all[0].Key == "b" && all[1].Key == "a" && all[1].Value == ToastKind.Bad, "a repeat moves to the end");
            h.Hold("match warn", ToastKind.Warn, true);
            h.Hold("match error", ToastKind.Bad, true);
            h.Hold("other warn", ToastKind.Warn, false);
            h.DropMatchWarnings();
            all = h.TakeAll();
            Check(all.Count == 2 && all.Any(n => n.Key == "match error") && all.Any(n => n.Key == "other warn"),
                  "DropMatchWarnings keeps errors and the rest");
        }

        static void InlineDispatcherRunsAtOnce()
        {
            var d = new InlineDispatcher();
            int n = 0;
            d.Post(() => n++);
            Check(n == 1 && d.OnUiThread, "inline: ran at once");
        }

        // Posted from another thread before the window has a handle: kept, and run once the handle exists.
        static void ControlDispatcherWaitsForTheHandle()
        {
            using (var c = new Control())
            {
                var d = new ControlDispatcher(c);
                int here = 0, there = 0;
                d.Post(() => here++);
                Check(here == 1, "on the UI thread: runs now");
                var t = new System.Threading.Thread(() => d.Post(() => there++));
                t.Start(); t.Join();
                Check(there == 0, "no handle yet: waits");
                Check(!c.IsHandleCreated, "still no handle");
                c.CreateControl();
                var h = c.Handle; // makes sure the handle exists
                for (int i = 0; i < 50 && there == 0; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                Check(there == 1, "ran once the handle exists: " + there);
                t = new System.Threading.Thread(() => d.Post(() => there++));
                t.Start(); t.Join();
                for (int i = 0; i < 50 && there == 1; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                Check(there == 2, "with a handle: BeginInvoke");
            }
            var gone = new Control();
            var dd = new ControlDispatcher(gone);
            gone.Dispose();
            int ran = 0;
            dd.Post(() => ran++);
            Check(ran == 0, "disposed: dropped");
        }

        static void StatsServiceRaisesOnceForTheGameOnScreen()
        {
            var dl = new Game { Name = "Deadlock", AppId = "1422450" };
            var ow = new Game { Name = "OW" };
            Game onScreen = dl;
            var clock = new DateTime(2026, 10, 14, 12, 0, 0);
            int fetched = 0, raised = 0;
            bool lastQuiet = false;
            var s = new StatsService(new InlineDispatcher(), g => { fetched++; return new PlayerStats { Steam = 5 }; }, () => onScreen,
                                     work => work(), () => clock);
            s.Updated += (g, st, quiet) => { raised++; lastQuiet = quiet; };
            Check(s.Current == PlayerStats.None && s.Current.Online == -1, "nothing yet: never null");
            Check(s.Stale(TimeSpan.FromMinutes(5)), "never loaded = stale");
            s.Load(dl, true);
            Check(fetched == 1 && raised == 1 && lastQuiet && s.Current.Steam == 5, "fetched and raised once");
            Check(!s.Stale(TimeSpan.FromMinutes(5)), "fresh right after");
            clock = clock.AddMinutes(4);
            Check(!s.Stale(TimeSpan.FromMinutes(5)), "4 min: still fresh");
            clock = clock.AddMinutes(1);
            Check(s.Stale(TimeSpan.FromMinutes(5)) && !s.Stale(TimeSpan.FromMinutes(6)), "5 min: stale for 5, fresh for 6");
            // another game came on screen while fetching: the numbers are dropped
            var before = s.Current;
            s.Load(ow, false);
            Check(fetched == 2 && raised == 1 && s.Current == before, "another game's numbers are not shown");
            Check(!s.Stale(TimeSpan.FromMinutes(1)), "the time counts from the start of the fetch, even when dropped");
        }

        static void PlayerStatsPlayersAndSummary()
        {
            var act = new Dictionary<string, int> { { "eu", 300 }, { "na", 100 } };
            var p = new PlayerStats { Activity = act, Online = 40000 };
            Check(p.PlayersOf(300) == 30000 && p.PlayersOf(100) == 10000, "online total split by match share: " + p.PlayersOf(300));
            var noTotal = new PlayerStats { Activity = act };
            Check(noTotal.PlayersOf(100) == Stats.EstimatePlayers(100), "without the total: the estimate");
            Check(PlayerStats.None.PlayersOf(10) == Stats.EstimatePlayers(10), "no numbers: the estimate");
            var dl = new Game { Name = "Deadlock", AppId = "1422450" };
            Check(p.Grouped(dl) && !p.Grouped(new Game { Name = "x" }) && !PlayerStats.None.Grouped(dl), "grouped only for Deadlock with numbers");
            Lang.Ar = false;
            string sum = p.Summary(dl);
            Check(sum.StartsWith("Online now (est.): ~40,000   |   ") && sum.IndexOf("~30,000") > 0, "Deadlock summary: " + sum);
            Check(new PlayerStats { Steam = 1234 }.Summary(new Game { Name = "x" }) == "Online now on Steam: 1,234 players (all regions together)", "Steam summary");
            Check(PlayerStats.None.Summary(new Game { Name = "c", Provider = Provider.Custom }) == "", "custom: empty");
            Check(PlayerStats.None.Summary(new Game { Name = "x" }) == "Player numbers aren't available for this game.", "nothing known");
            Lang.Ar = true;
        }

        static void UpdateServicePromptsOnceAndNeverMidMatch()
        {
            Release next = null;
            bool isForced = false, running = false;
            var u = new UpdateService(new InlineDispatcher(), () => next, r => isForced, () => running, work => work());
            int changed = 0, available = 0, must = 0, prompt = 0;
            u.Changed += r => changed++;
            u.Available += r => available++;
            u.MustUpdate += r => must++;
            u.FirstPrompt += r => prompt++;
            u.Check(true);
            Check(changed == 1 && available == 0 && prompt == 0 && u.Pending == null, "up to date: the bar only");
            next = new Release { Tag = "v1.2.3-beta", Ver = new Version(1, 2, 3) };
            u.Check(false);
            u.Check(false);
            Check(changed == 3 && available == 2 && prompt == 1 && must == 0 && u.Pending == next, "the first prompt only once: " + prompt);
            // forced, the game running, not at start: wait for the next launch
            isForced = true; running = true;
            u.Check(false);
            Check(must == 0 && available == 3, "forced mid-match: no MustUpdate");
            u.Check(true);
            Check(must == 1, "forced at start: MustUpdate even with the game open");
            running = false;
            u.Check(false);
            Check(must == 2, "forced, game closed: MustUpdate");
            // closing to install: the bar still follows, nothing else
            u.ClosingForUpdate = true;
            u.Check(true);
            Check(changed == 7 && must == 2 && available == 5, "closing for an update: no more prompts");
            next = null;
            u.ClosingForUpdate = false;
            u.Check(false);
            Check(u.Pending == null && changed == 8, "back to up to date clears Pending");
        }

        static void NoticeCenterHoldsWarningsWhileMinimized()
        {
            bool min = false, simpleLayout = false;
            var shown = new List<string>();
            int flashes = 0;
            var n = new NoticeCenter(new InlineDispatcher(), () => min, () => simpleLayout, (t, k) => shown.Add(t + ":" + k), () => flashes++);
            n.Say("hello", ToastKind.Info, false);
            Check(shown.Count == 1 && shown[0] == "hello:Info", "restored: shown");
            min = true;
            n.Say("info", ToastKind.Info, false);
            n.Say("good", ToastKind.Good, false);
            Check(shown.Count == 1 && n.HeldCount == 0 && flashes == 0, "minimized + Info/Good is dropped");
            n.Say("warn", ToastKind.Warn, false);
            n.Say("bad", ToastKind.Bad, true);
            n.SayAdvanced("match warn", ToastKind.Warn);
            Check(shown.Count == 1 && n.HeldCount == 3 && flashes == 3, "minimized + Warn/Bad is held and flashes");
            n.MatchChanged();
            Check(n.HeldCount == 2, "the match changed: its warning goes, the error stays");
            n.FlushHeld();
            Check(shown.Count == 1, "still minimized: nothing shown");
            min = false;
            n.FlushHeld();
            Check(shown.Count == 3 && shown[1] == "warn:Warn" && shown[2] == "bad:Bad" && n.HeldCount == 0, "back: the held ones, oldest first");
            // the simple layout leaves advanced notices out, unless minimized
            simpleLayout = true;
            n.SayAdvanced("advanced", ToastKind.Info);
            Check(shown.Count == 3, "simple + SayAdvanced: dropped");
            simpleLayout = false;
            n.SayAdvanced("advanced", ToastKind.Info);
            Check(shown.Count == 4, "advanced layout: shown");
            simpleLayout = true; min = true;
            n.SayAdvanced("while away", ToastKind.Warn);
            Check(n.HeldCount == 1, "simple + minimized + warning: held");
        }

        sealed class FakeProbe : IPingProbe
        {
            public Dictionary<string, double> Ms = new Dictionary<string, double>(); // host -> ping (missing or < 0 = no answer)
            public double Relay = 10, Behind = 25;
            public bool Throw;
            public PingResult Measure(Endpoint ep, int count, int timeoutMs)
            {
                if (Throw) throw new InvalidOperationException("probe broke");
                double v;
                lock (Ms) if (!Ms.TryGetValue(ep.Host, out v)) v = -1;
                return v < 0 ? new PingResult() : new PingResult { Avg = v, Loss = 0 };
            }
            public double RelayPing(Relay relay) { return Relay; }
            public double FromRelay(Relay relay, Endpoint target) { return Behind; }
        }

        static Region Endpoints(string id, params string[] hosts)
        {
            var r = new Region { Id = id, NameEn = id };
            foreach (var h in hosts) r.Endpoints.Add(new Endpoint(h, 27015));
            return r;
        }

        static void PingTestPicksTheBestAddressAndReportsOnce()
        {
            var probe = new FakeProbe();
            probe.Ms["a1"] = 80; probe.Ms["a2"] = 30; probe.Ms["b1"] = -1;
            var g = new Game { Name = "G" };
            var a = Endpoints("a", "a1", "a2", "a3");
            var b = Endpoints("b", "b1");
            var t = new PingTestService(new InlineDispatcher(), probe, ex => { }, work => work());
            int relaysMeasured = 0, done = 0, completed = 0;
            Game testingDuringCompleted = g;
            long tookMs = -1;
            t.RelaysMeasured += () => relaysMeasured++;
            t.RegionDone += (tg, r, n, total) => { System.Threading.Interlocked.Increment(ref done); };
            t.Completed += (tg, regions, ms) => { completed++; testingDuringCompleted = t.Testing; tookMs = ms; };
            t.Prepare(g);
            Check(t.Testing == g, "prepared: Testing says so before it runs");
            var relays = new List<Relay> { new Relay { Name = "fra", AgentUrl = "http://1.2.3.4:8080", Token = "t" }, new Relay { Name = "noagent" } };
            t.Run(new List<Region> { a, b }, relays);
            Check(relaysMeasured == 1 && done == 2 && completed == 1, "relays once, each region once, completed once");
            Check(testingDuringCompleted == null && t.Testing == null && tookMs >= 0, "Testing cleared before Completed");
            Check(a.Direct.Ok && a.Direct.Avg == 30 && a.BestEndpoint.Host == "a2", "the lowest answering address: " + a.BestEndpoint);
            Check(!b.Direct.Ok && b.BestEndpoint == null, "no answer: no best address");
            Check(relays[0].Ping == 10 && a.ViaRelay.Count == 1 && a.ViaRelay["fra"] == 35, "relay ping + the relay's own ping");
            Check(!a.ViaRelay.ContainsKey("noagent"), "a relay without an agent can't probe");
            var empty = Endpoints("none");
            PingTestService.Measure(empty, new List<Relay>(), probe);
            Check(empty.Direct != null && !empty.Direct.Ok && empty.BestEndpoint == null, "no addresses: an empty result");
        }

        static void PingTestFailureIsReportedThenRaised()
        {
            var probe = new FakeProbe { Throw = true };
            var g = new Game { Name = "G" };
            var order = new List<string>();
            var t = new PingTestService(new InlineDispatcher(), probe, ex => order.Add("crashed"), work => work());
            t.Failed += (tg, ex) => order.Add("failed " + (t.Testing == null ? "cleared" : "still set"));
            t.Completed += (tg, r, ms) => order.Add("completed");
            t.Prepare(g);
            t.Run(new List<Region> { Endpoints("a", "a1") }, new List<Relay>());
            Check(string.Join(",", order.ToArray()) == "crashed,failed cleared", "crash report, then Failed with Testing cleared: " + string.Join(",", order.ToArray()));
            Check(PingTestService.Parallel == 16, "16 regions at a time");
        }

        static void GameSessionLoadsTheGameOnScreen()
        {
            var g = new Game { Name = "G", Processes = new[] { "g" } };
            var other = new Game { Name = "O" };
            Game onScreen = g;
            int attempts = 0, slept = 0, failAttempts = 0;
            bool open = false, autoTest = true;
            var s = new GameSession(new InlineDispatcher(),
                game => { attempts++; if (attempts <= failAttempts) throw new Exception("steam slow " + attempts); game.Regions.Add(new Region { Id = "r" }); },
                game => open, () => onScreen, work => work(), ms => slept += ms);
            string failed = null;
            int loaded = 0;
            bool lastOpen = false;
            s.Failed += (game, err) => failed = err;
            s.Loaded += (game, o) => { loaded++; lastOpen = o; };
            // one quiet retry
            failAttempts = 1;
            s.Load(g, () => autoTest);
            Check(attempts == 2 && slept == 1500 && failed == null && loaded == 1 && s.IsLoaded(g) && s.LoadFailed == null, "one retry, then loaded");
            // two failures: LoadFailed, Failed raised with the last error
            var h = new Game { Name = "H" };
            onScreen = h; attempts = 0; failAttempts = 2;
            s.Load(h, () => autoTest);
            Check(failed == "steam slow 2" && s.LoadFailed == h && !s.IsLoaded(h) && loaded == 1, "load failure -> LoadFailed: " + failed);
            // loaded but no servers: LoadFailed too, and Loaded is still raised
            var empty = new Game { Name = "E" };
            onScreen = empty; failAttempts = 0; attempts = 0;
            var noServers = new GameSession(new InlineDispatcher(), game => { }, game => false, () => onScreen, work => work(), ms => { });
            int emptyLoaded = 0;
            noServers.Loaded += (game, o) => emptyLoaded++;
            noServers.Load(empty, () => true);
            Check(noServers.LoadFailed == empty && noServers.IsLoaded(empty) && emptyLoaded == 1, "no servers -> LoadFailed");
            // another game on screen by the time it's loaded: nothing is raised
            onScreen = other; loaded = 0; failed = null;
            s.Load(g, () => autoTest);
            Check(loaded == 0 && failed == null, "another game on screen: dropped");
            // the game is open: only asked with the automatic test on
            onScreen = g; open = true;
            s.Load(g, () => autoTest);
            Check(lastOpen, "open with auto test -> gameOpen");
            autoTest = false;
            s.Load(g, () => autoTest);
            Check(!lastOpen, "auto test off: never 'open'");
        }

        static void GameSessionRetryForgetsAndTestsAfterLoad()
        {
            var g = new Game { Name = "G" };
            var s = new GameSession(new InlineDispatcher(), game => game.Regions.Add(new Region { Id = "r" }), game => false, () => g, work => work(), ms => { });
            s.Load(g, () => true);
            s.MarkTested(g);
            s.SkippedAutoTest(g);
            Check(s.IsLoaded(g) && s.Tested(g) && s.NotAutoTested(g), "loaded, tested, skipped");
            s.Retry(g);
            Check(!s.IsLoaded(g) && !s.Tested(g) && s.RetestAfterLoad == g, "Offline retry: forget loaded + tested, test after load");
            Check(s.ClearSkippedAutoTest(g) && !s.ClearSkippedAutoTest(g) && !s.NotAutoTested(g), "skipped flag cleared once");
        }

        static void MatchStateSaysWhenTheMatchChanged()
        {
            var m = new MatchState();
            Check(!m.Any && m.Ms == -1, "empty");
            Check(m.Set("Europe", true, false, null, "Frankfurt"), "first match: different");
            Check(m.Any && m.Region == "Europe" && m.OnPicked && m.Place == "Frankfurt", "set");
            Check(!m.Set("Europe", true, false, "x", "Frankfurt"), "same region, pick, relay and place: the same match (entry doesn't count)");
            Check(m.Entry == "x", "entry still updated");
            Check(m.Set("Europe", true, false, null, "Vienna"), "another server: a different match");
            Check(m.Set("Europe", false, false, null, "Vienna"), "on picked changed: different");
            Check(m.Set(null, false, true, "Dubai", null), "through a relay: different");
            Check(m.Any && m.ViaRelay && m.Entry == "Dubai", "via relay counts as a match");
            m.Clear();
            Check(!m.Any && m.Region == null && m.Entry == null && m.Place == null && !m.OnPicked && !m.ViaRelay && m.Server == null && m.Ms == -1, "cleared");
            m.SetServer("Bahrain", 31);
            Check(m.Any && m.Server == "Bahrain" && m.Ms == 31, "a server alone counts");
        }

        static void ZoneMemoryCachesAndLearns()
        {
            var settings = new Dictionary<string, string>();
            int reads = 0, writes = 0;
            var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            var mem = new ZoneMemory(k => { reads++; string v; return settings.TryGetValue(k, out v) ? v : ""; },
                                     (k, v) => { writes++; settings[k] = v; }, () => now);
            var ow = new Game { Name = "OW", Icon = "overwatch", LockSpace = new[] { "x" },
                                RegionMap = new List<RegionDef> { RegionDef.Of("sg", "Singapore", "Singapore", "GSG1").In(Zones.Asia),
                                                                  RegionDef.Of("eu", "Europe", "Europe", "GEW3").In(Zones.Europe) } };
            Check(ZoneMemory.Key(ow) == "zones_seen_overwatch", "the settings key is unchanged");
            Check(mem.Seen(ow).Count == 0 && reads == 1, "nothing seen yet");
            mem.Seen(ow);
            Check(reads == 1, "cached: settings read once");
            mem.Learn(ow, new[] { new Region { Id = "GSG1" } });
            Check(writes == 1 && settings["zones_seen_overwatch"] == "as:" + now.Ticks, "learned Asia: " + settings["zones_seen_overwatch"]);
            Check(mem.Seen(ow).SequenceEqual(new[] { "as" }), "learning clears the cache");
            mem.Learn(ow, new[] { new Region { Id = "GSG1" } });
            Check(writes == 1, "seen within the hour: no write");
            mem.Learn(ow, new[] { new Region { Id = "unknown" } });
            Check(writes == 1, "a datacenter in no zone: nothing");
            var valve = new Game { Name = "V", Icon = "deadlock", Provider = Provider.Valve };
            mem.Learn(valve, new[] { new Region { Id = "fra" } });
            Check(writes == 1 && !settings.ContainsKey("zones_seen_deadlock"), "games without zones learn nothing");
            // an old plain list is saved once in the new form
            settings["zones_seen_ow2"] = "eu";
            var old = new Game { Name = "OW2", Icon = "ow2", RegionMap = ow.RegionMap };
            Check(mem.Seen(old).SequenceEqual(new[] { "eu" }) && settings["zones_seen_ow2"] == "eu:" + now.Ticks, "migrated");
            // the cache lasts an hour
            int before = reads;
            now = now.AddMinutes(59);
            mem.Seen(old);
            Check(reads == before, "59 min: cached");
            now = now.AddMinutes(2);
            mem.Seen(old);
            Check(reads == before + 1, "an hour later: read again");
        }

        static void RelayStoreAddRemoveRoundTrip()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "2xping-relays-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp);
            try
            {
                string conf = System.IO.Path.Combine(tmp, "client.conf");
                System.IO.File.WriteAllText(conf, "[Peer]\nEndpoint = 203.0.113.7:51820\n");
                int saves = 0;
                List<Relay> saved = null;
                var store = new RelayStore(new List<Relay>(), System.IO.Path.Combine(tmp, "relays"), l => { saves++; saved = l.ToList(); });
                var r = store.Add("My Relay/1", conf, "http://203.0.113.7:8787", "tok");
                Check(saves == 1 && saved.Count == 1 && store.All.Count == 1 && store.All[0] == r, "added and saved");
                Check(r.ConfPath == System.IO.Path.Combine(tmp, "relays", "My_Relay_1.conf") && System.IO.File.Exists(r.ConfPath), "config copied: " + r.ConfPath);
                Check(r.EndpointHost == "203.0.113.7", "the copy reads like the original");
                Check(store.Has("my relay/1") && !store.Has("other"), "names compare without case");
                Check(RelayStore.SafeName("Dubaï 2") == "Duba__2", "non-ASCII and spaces become _: " + RelayStore.SafeName("Dubaï 2"));
                store.Remove(r);
                Check(saves == 2 && saved.Count == 0 && store.All.Count == 0, "removed and saved");
            }
            finally { try { System.IO.Directory.Delete(tmp, true); } catch { } }
        }

        static bool WaitFor(Func<bool> cond)
        {
            for (int i = 0; i < 300; i++) { if (cond()) return true; System.Threading.Thread.Sleep(10); }
            return cond();
        }

        static void LiveMonitorStopsAndRestarts()
        {
            var samples = new List<string>();
            var gate = new System.Threading.ManualResetEventSlim(true);
            int pings = 0;
            var m = new LiveMonitor(new InlineDispatcher(), () => true,
                                    (ep, timeout) => { gate.Wait(); System.Threading.Interlocked.Increment(ref pings); return 42; },
                                    ms => System.Threading.Thread.Sleep(5), () => DateTime.Now);
            m.Sample += (label, v) => { lock (samples) samples.Add(label + ":" + v); };
            m.Start(new Endpoint("a", 0), "A");
            Check(WaitFor(() => { lock (samples) return samples.Count >= 3; }), "samples come in");
            // a ping in flight when Stop comes: its answer is dropped
            gate.Reset();
            int inFlight = pings;
            System.Threading.Thread.Sleep(30);
            m.Stop();
            int count;
            lock (samples) count = samples.Count;
            gate.Set();
            System.Threading.Thread.Sleep(100);
            lock (samples) Check(samples.Count == count, "Stop -> no Sample after: " + (samples.Count - count));
            // started twice: only the new label samples
            m.Start(new Endpoint("a", 0), "A");
            m.Start(new Endpoint("b", 0), "B");
            lock (samples) samples.Clear();
            Check(WaitFor(() => { lock (samples) return samples.Count >= 3; }), "the new one samples");
            m.Stop();
            System.Threading.Thread.Sleep(50);
            lock (samples) Check(samples.All(x => x == "B:42"), "only B after the restart: " + string.Join(",", samples.ToArray()));
        }

        static void LiveMonitorSlowsDownWhileTheGameIsInFront()
        {
            var clock = new DateTime(2026, 10, 14, 12, 0, 0);
            int pings = 0;
            var stepped = new System.Threading.AutoResetEvent(false);
            LiveMonitor m = null;
            m = new LiveMonitor(new InlineDispatcher(), () => false,
                                (ep, t) => { System.Threading.Interlocked.Increment(ref pings); return 10; },
                                ms => { lock (stepped) clock = clock.AddMilliseconds(ms); if (pings >= 3) m.Stop(); System.Threading.Thread.Sleep(1); },
                                () => { lock (stepped) return clock; });
            var start = clock;
            m.Start(new Endpoint("a", 0), "A");
            Check(WaitFor(() => pings >= 3), "pings while in the background");
            System.Threading.Thread.Sleep(50);
            DateTime end;
            lock (stepped) end = clock;
            // 3 pings with the game in front: 15 s apart (the first at once)
            Check(pings == 3 && end - start >= TimeSpan.FromSeconds(30), "one ping per 15 s: " + pings + " in " + (end - start).TotalSeconds + " s");
        }

        static void ListStyleFirstCellFollowsTheReadingOrder()
        {
            Lang.Ar = false;
            using (var en = ListStyle.Make(new[] { "a", "b", "c" }, new[] { 100, 50, 30 }, () => SystemFonts.DefaultFont, e => false, -1))
            {
                var row = new Rectangle(0, 10, 180, 30);
                var cell = ListStyle.FirstCell(en, new ListViewItem("x"), row);
                Check(cell == new Rectangle(0, 10, 100, 30), "English: the first column on the left: " + cell);
                Check(ListStyle.CheckBox(cell).X == 8, "English: the tick box on the left");
                Check(ListStyle.Align == TextFormatFlags.Left, "English: left-aligned");
            }
            Lang.Ar = true;
            using (var ar = ListStyle.Make(new[] { "a", "b", "c" }, new[] { 100, 50, 30 }, () => SystemFonts.DefaultFont, e => false, -1))
            {
                Check(ar.Columns[0].DisplayIndex == 2 && ar.Columns[2].DisplayIndex == 0, "Arabic: the columns reversed");
                Check(ar.RightToLeft == RightToLeft.No, "the list itself stays left-to-right");
                var cell = ListStyle.FirstCell(ar, new ListViewItem("x"), new Rectangle(0, 10, 180, 30));
                Check(cell == new Rectangle(80, 10, 100, 30), "Arabic: the first column on the right: " + cell);
                Check(ListStyle.CheckBox(cell).Right == cell.Right - 8, "Arabic: the tick box on the right");
                Check(ListStyle.Align == (TextFormatFlags.Right | TextFormatFlags.RightToLeft), "Arabic: right-aligned RTL");
            }
            var sel = new ListViewItem("s");
            Check(ListStyle.RowBack(sel, 0) == Theme.Panel1 && ListStyle.RowBack(sel, 1) == Theme.RowAlt, "striped rows");
        }

        static void DeadlockLogReadsAndLooksAgainEveryTenMinutes()
        {
            string path = TempLog();
            try
            {
                var dl = new Game { Name = "Deadlock", Icon = "deadlock", Processes = new[] { "deadlock" } };
                var clock = new DateTime(2026, 10, 14, 20, 0, 0);
                var started = clock.AddMinutes(-30);
                int lookups = 0;
                string where = null; // not found yet
                var log = new DeadlockLog(g => { lookups++; return where; }, g => started, ex => { }, () => clock);
                DateTime s, readAt;
                Check(log.Read(dl, out s, out readAt) == null && readAt == DateTime.MinValue && s == started && lookups == 1 && !log.On, "no log yet");
                where = path;
                clock = clock.AddMinutes(5);
                log.Read(dl, out s, out readAt);
                Check(lookups == 1, "looked for again only after 10 minutes");
                File.WriteAllText(path, "Ping location: fra=20\n", new UTF8Encoding(false));
                clock = clock.AddMinutes(6);
                Check(log.Read(dl, out s, out readAt) == null && lookups == 2 && readAt == clock && log.On, "found: read, log on");
                Append(path, "    Remote host is in data center 'fra'\n");
                clock = clock.AddMinutes(1);
                var line = log.Read(dl, out s, out readAt);
                Check(line != null && line.Code == "fra" && line.At == clock && lookups == 2, "a new match line");
                Check(log.Fresh(line, started, DateTime.MinValue) == line, "fresh for this game");
                log.NoMatchSeen(clock.AddSeconds(-5), clock);
                Check(log.Fresh(line, started, DateTime.MinValue) == null, "a later check saw no match: stale");
                Check(log.Read(new Game { Name = "x" }, out s, out readAt) == null && readAt == DateTime.MinValue, "other games: no log");
                File.Delete(path);
                clock = clock.AddMinutes(1);
                log.Read(dl, out s, out readAt);
                Check(!log.On, "the file is gone: off");
                // the saved exe path at startup
                Check(!log.PeekSaved(null), "no saved exe");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        static void DeadlockLogResolvesHostingDatacenters()
        {
            var g = SavedDeadlock();
            foreach (var r in g.Regions) r.Picked = r.Id == "fra";
            string id, region;
            Region dc;
            bool onPicked;
            Check(DeadlockLog.Resolve(g, new ConsoleLine { Code = "fra" }, out id, out dc, out region, out onPicked), "Frankfurt hosts");
            Check(id == "fra" && dc != null && dc.Id == "fra" && onPicked && region != null, "Frankfurt, picked: " + region);
            Check(DeadlockLog.Resolve(g, new ConsoleLine { Code = "vie" }, out id, out dc, out region, out onPicked) && !onPicked, "Vienna not picked");
            Check(!DeadlockLog.Resolve(g, new ConsoleLine { Code = "dxb" }, out id, out dc, out region, out onPicked), "Dubai is a relay only: no answer");
            Check(!DeadlockLog.Resolve(g, new ConsoleLine { Code = "zzz" }, out id, out dc, out region, out onPicked), "unknown place: no answer");
        }

        static void FrontTrackerKnowsWhoIsInFront()
        {
            uint fg = 100;
            var clock = new DateTime(2026, 10, 14, 12, 0, 0);
            var names = new Dictionary<uint, string> { { 200, "project8" }, { 300, "chrome" } };
            var t = new FrontTracker(() => fg, pid => { string n; return names.TryGetValue(pid, out n) ? n : null; }, 100, () => clock);
            var dl = new Game { Name = "Deadlock", Processes = new[] { "Project8" } };
            Check(t.AppFront, "2X Ping in front");
            Check(!t.GameInFront(new[] { dl }), "2X Ping is no game");
            fg = 200;
            Check(!t.AppFront && t.GameInFront(new[] { dl }), "the game in front (names compare without case)");
            fg = 300;
            Check(!t.GameInFront(new[] { dl }), "a browser is no game");
            fg = 400;
            Check(!t.GameInFront(new[] { dl }), "a process that's gone: no");
            fg = 0;
            Check(!t.GameInFront(new[] { dl }), "no foreground window: no");
            // coming back counts only after 20 s away
            clock = clock.AddSeconds(19);
            Check(!t.CameBack(), "19 s away: not a return");
            clock = clock.AddSeconds(1);
            Check(t.CameBack(), "20 s away: a return");
            fg = 100;
            Check(t.AppFront && !t.CameBack(), "seen in front again: the absence starts over");
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
            Run("LossTrackerReportsFifteenPercent", LossTrackerReportsFifteenPercent);
            Run("HeldNoticesKeepFourWithoutRepeats", HeldNoticesKeepFourWithoutRepeats);
            Run("InlineDispatcherRunsAtOnce", InlineDispatcherRunsAtOnce);
            Run("ControlDispatcherWaitsForTheHandle", ControlDispatcherWaitsForTheHandle);
            Run("StatsServiceRaisesOnceForTheGameOnScreen", StatsServiceRaisesOnceForTheGameOnScreen);
            Run("PlayerStatsPlayersAndSummary", PlayerStatsPlayersAndSummary);
            Run("UpdateServicePromptsOnceAndNeverMidMatch", UpdateServicePromptsOnceAndNeverMidMatch);
            Run("NoticeCenterHoldsWarningsWhileMinimized", NoticeCenterHoldsWarningsWhileMinimized);
            Run("PingTestPicksTheBestAddressAndReportsOnce", PingTestPicksTheBestAddressAndReportsOnce);
            Run("PingTestFailureIsReportedThenRaised", PingTestFailureIsReportedThenRaised);
            Run("GameSessionLoadsTheGameOnScreen", GameSessionLoadsTheGameOnScreen);
            Run("GameSessionRetryForgetsAndTestsAfterLoad", GameSessionRetryForgetsAndTestsAfterLoad);
            Run("MatchStateSaysWhenTheMatchChanged", MatchStateSaysWhenTheMatchChanged);
            Run("ZoneMemoryCachesAndLearns", ZoneMemoryCachesAndLearns);
            Run("RelayStoreAddRemoveRoundTrip", RelayStoreAddRemoveRoundTrip);
            Run("LiveMonitorStopsAndRestarts", LiveMonitorStopsAndRestarts);
            Run("LiveMonitorSlowsDownWhileTheGameIsInFront", LiveMonitorSlowsDownWhileTheGameIsInFront);
            Run("ListStyleFirstCellFollowsTheReadingOrder", ListStyleFirstCellFollowsTheReadingOrder);
            Run("DeadlockLogReadsAndLooksAgainEveryTenMinutes", DeadlockLogReadsAndLooksAgainEveryTenMinutes);
            Run("DeadlockLogResolvesHostingDatacenters", DeadlockLogResolvesHostingDatacenters);
            Run("FrontTrackerKnowsWhoIsInFront", FrontTrackerKnowsWhoIsInFront);
        }
    }
}
