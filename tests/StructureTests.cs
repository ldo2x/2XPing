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
        }
    }
}
