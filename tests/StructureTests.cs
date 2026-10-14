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

        static void RunStructureTests()
        {
            Run("RoutingRelayMarginIsThreeMs", RoutingRelayMarginIsThreeMs);
            Run("RoutingDirectDownTakesRelay", RoutingDirectDownTakesRelay);
            Run("RoutingBestAndSort", RoutingBestAndSort);
            Run("FmtShortNameAndPing", FmtShortNameAndPing);
        }
    }
}
