using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // The strict, route-aware lock (Deadlock audit I1/I3/I5, ruling R3, guard G5), on the saved SDR config
    // (tests/data/sdr-deadlock.json) and the owner's own pings from Riyadh on STC (audit §1.5, ICMP, no lock).
    static partial class Tests
    {
        static Dictionary<string, double> RiyadhPings()
        {
            return new Dictionary<string, double>
            {
                {"dxb",31}, {"dfra",68}, {"fra",74}, {"ams",73}, {"mad",73}, {"lhr",78}, {"dvie",82}, {"par",83}, {"vie",85},
                {"waw",90}, {"sto",94}, {"sto2",105}, {"bom2",159}, {"dfjr",161}, {"sgp",197}, {"jnb",224}, {"hkg",224}, {"maa2",334},
            };
        }

        static List<string> RegionIds(Game g, string key)
        {
            return g.RegionMap.First(d => d.Key == key).Ids.ToList();
        }

        static bool Near(double got, double want) { return Math.Abs(got - want) <= 3; }

        static void RoutesShortestPaths()
        {
            var g = SavedDeadlock();
            var net = g.Routes;
            Check(net.Dist("dxb", "dxb") == 0 && net.Dist("zzz", "zzz") == 0, "same place: 0");
            Check(net.Dist("dxb", "zzz") == -1, "unknown place: -1");
            Check(g.Backbone.ContainsKey("dxb sgp") && net.Dist("dxb", "sgp") <= g.Backbone["dxb sgp"] && net.Dist("dxb", "sgp") == 75,
                  "a direct pair is at most Valve's number (a shorter path wins): " + net.Dist("dxb", "sgp"));
            Check(!g.Backbone.ContainsKey("dxb fra") && net.Dist("dxb", "fra") > 0, "dxb-fra is missing from typical_pings but has a path");
            Check(net.Dist("dxb", "vie") == net.Dist("vie", "dxb"), "symmetric");
            // the audit's table (§3.1): via Dubai from Riyadh
            var ping = RiyadhPings();
            foreach (var t in new[] { new object[] { "fra", 69 }, new object[] { "vie", 58 }, new object[] { "waw", 69 }, new object[] { "sto", 82 },
                                      new object[] { "fsn", 67 }, new object[] { "hel", 87 }, new object[] { "sgp", 106 }, new object[] { "hkg", 137 } })
                Check(31 + net.Dist("dxb", (string)t[0]) == (int)t[1], "via dxb to " + t[0] + ": " + (31 + net.Dist("dxb", (string)t[0])));
            // route ping = min over open POPs of ping + path (the datacenter itself with 0)
            var r = StrictLock.Best(net, new[] { "sgp" }, ping, null);
            Check(r.Ms == 106 && r.Entry == "dxb" && r.To == "sgp", "SE Asia from Riyadh: 106 via dxb, got " + r.Ms + " via " + r.Entry);
            r = StrictLock.Best(net, new[] { "sgp" }, ping, id => id == "sgp" || id == "hkg" || id == "maa2");
            Check(r.Ms == 197 && r.Entry == "sgp", "with the relays closed: 197 direct, got " + r.Ms + " via " + r.Entry);
            r = StrictLock.Best(net, new[] { "jnb" }, ping, null);
            Check(r.Ms == 155 && r.Entry != "jnb", "jnb: 155 through Europe (not 224 direct), got " + r.Ms + " via " + r.Entry);
            r = StrictLock.Best(net, new[] { "vie" }, ping, null);
            Check(r.Ms == 58 && r.Entry == "dxb", "vie: 58 via dxb (the direct 85 loses)");
            // equal: the direct way wins
            var tie = SdrRoutes.Build(new Dictionary<string, int> { { "a b", 10 } });
            r = StrictLock.Best(tie, new[] { "b" }, new Dictionary<string, double> { { "a", 20 }, { "b", 30 } }, null);
            Check(r.Ms == 30 && r.Entry == "b", "a tie goes to the direct way");
            Check(!StrictLock.Best(net, new[] { "sgp" }, new Dictionary<string, double>(), null).Known, "no pings: no route");
        }

        static void StrictLockEuropeKeepsDubai()
        {
            var g = SavedDeadlock();
            var plan = StrictLock.For(g, RegionIds(g, "Europe"), RiyadhPings());
            Check(plan.Close.Count == 0 && !plan.MayLeak, "Europe from Riyadh closes nothing: " + string.Join(",", plan.Close.ToArray()));
            Check(plan.Locked.Ms == 58 && plan.Locked.Entry == "dxb", "Europe stays 58 via dxb, got " + plan.Locked.Ms + " via " + plan.Locked.Entry);
            Check(MainForm.NoteOf(g, plan, "Europe") == null, "nothing to tell");
        }

        static void StrictLockSeAsiaClosesDubai()
        {
            var g = SavedDeadlock();
            var plan = StrictLock.For(g, RegionIds(g, "SEAsia"), RiyadhPings());
            Check(plan.Close.Contains("dxb") && plan.Close.Contains("dfjr"), "dxb and dfjr closed: " + string.Join(",", plan.Close.ToArray()));
            Check(plan.Close.Contains("bom2") && !plan.Close.Contains("maa2"), "bom2 (213 to dvie) too, maa2 (365) not: " + string.Join(",", plan.Close.ToArray()));
            Check(plan.Reason["dxb"].Ms == 58 && plan.Reason["dxb"].To == "dvie", "dxb reaches Europe in 58");
            Check(!plan.MayLeak, "sgp answers: strict");
            Check(Near(plan.Unlocked.Ms, 106) && plan.Unlocked.Entry == "dxb", "unlocked ~106 via dxb: " + plan.Unlocked.Ms);
            Check(Near(plan.Locked.Ms, 197) && plan.Locked.Entry == "sgp", "locked ~197 direct: " + plan.Locked.Ms + " via " + plan.Locked.Entry);
            // the lock blocks the closed relays' addresses
            var cidrs = g.Regions.Where(r => plan.Close.Contains(r.Id)).SelectMany(r => r.Cidrs).ToList();
            Check(cidrs.Count > 0 && cidrs.Contains("185.25.183.163/32"), "dxb's relay is in the block list");

            Lang.Ar = true;
            var note = MainForm.NoteOf(g, plan, "جنوب شرق آسيا");
            Check(note != null && note.Kind == RouteNoteKind.ClosedEntry, "a closed best entry is told");
            string ar = CardLogic.RouteText(note);
            Check(ar == "لازم نقفل دبي لأنها توصل لأوروبا. بنقك لجنوب شرق آسيا بيصير تقريباً 197 بدل 106.", "Arabic: " + ar);
            Lang.Ar = false;
            note = MainForm.NoteOf(g, plan, "SE Asia");
            string en = CardLogic.RouteText(note);
            Check(en == "We have to close Dubai because it reaches Europe. Your ping to SE Asia becomes about 197 instead of 106.", "English: " + en);
            Lang.Ar = true;
        }

        static void StrictLockNorthAmericaFromRiyadh()
        {
            var g = SavedDeadlock();
            var na = RegionIds(g, "Row");
            // the owner's list has no US datacenter: the only ways there go through relays that reach Europe first
            var plan = StrictLock.For(g, na, RiyadhPings());
            Check(plan.MayLeak && plan.Kept == "dxb", "G5 keeps dxb open: kept " + plan.Kept + ", closed " + string.Join(",", plan.Close.ToArray()));
            Check(!plan.Close.Contains("dxb") && plan.Close.Contains("dfjr"), "dxb open, dfjr closed");
            Check(Near(plan.Locked.Ms, 154) && plan.Locked.Entry == "dxb", "NA ~154 via dxb: " + plan.Locked.Ms);
            Lang.Ar = true;
            var note = MainForm.NoteOf(g, plan, "أمريكا الشمالية");
            Check(note.Kind == RouteNoteKind.MayLeak, "may-leak note");
            Check(CardLogic.RouteText(note) == "لازم نخلي دبي مفتوحة، وإلا اللعبة ما بتوصل لأمريكا الشمالية أبداً. بس دبي توصل لأوروبا بعد، فالتثبيت ممكن ما يمسك.",
                  "Arabic: " + CardLogic.RouteText(note));
            Lang.Ar = false;
            note = MainForm.NoteOf(g, plan, "North America");
            Check(CardLogic.RouteText(note) == "We have to keep Dubai open, or the game can't reach North America at all. Dubai also reaches Europe, so the lock may not hold.",
                  "English: " + CardLogic.RouteText(note));
            Lang.Ar = true;

            // with a measured US ping (190 to iad: an assumption, not in §1.5) the lock is strict
            var ping = RiyadhPings();
            ping["iad"] = 190;
            plan = StrictLock.For(g, na, ping);
            Check(!plan.MayLeak && plan.Close.Contains("dxb") && plan.Close.Contains("dfjr"), "strict: " + string.Join(",", plan.Close.ToArray()));
            Check(plan.Locked.Ms == 190 && plan.Locked.Entry == "iad", "iad direct 190: " + plan.Locked.Ms);
            Check(Near(plan.Unlocked.Ms, 153) && plan.Unlocked.Entry == "dfra", "unlocked: ~153 via Datapacket Frankfurt: " + plan.Unlocked.Ms + " " + plan.Unlocked.Entry);
            // the best way in is a European datacenter the lock blocks anyway (I3): pick Europe too to keep it
            Lang.Ar = false;
            note = MainForm.NoteOf(g, plan, "North America");
            Check(note.Kind == RouteNoteKind.BlockedHost && note.Entry == "Europe", "blocked host note: " + (note == null ? "-" : note.Kind + " " + note.Entry));
            Check(CardLogic.RouteText(note) == "Your shortest way to North America goes through Europe, and the lock closes it: your ping becomes about 190 instead of 153. Pick Europe too to keep it.",
                  "English: " + CardLogic.RouteText(note));
            Lang.Ar = true;
            Check(CardLogic.RouteText(MainForm.NoteOf(g, plan, "أمريكا الشمالية")) ==
                  "أقصر طريق لك لأمريكا الشمالية يمر من أوروبا، والتثبيت يقفله: بنقك بيصير تقريباً 190 بدل 153. اختار أوروبا بعد عشان يبقى مفتوح.",
                  "Arabic: " + CardLogic.RouteText(MainForm.NoteOf(g, plan, "أمريكا الشمالية")));
        }

        // A small network: picked p, a left-out x, an entry relay e.
        static LockPlan Tiny(double pingE, double pingP, int ex, int ep)
        {
            var net = SdrRoutes.Build(new Dictionary<string, int> { { "e x", ex }, { "e p", ep } });
            var ping = new Dictionary<string, double> { { "e", pingE } };
            if (pingP >= 0) ping["p"] = pingP;
            return StrictLock.Plan(net, ping, new[] { "p" }, new HashSet<string> { "x" }, new HashSet<string> { "x" }, new[] { "e" }, StrictLock.MarginMs);
        }

        static void StrictLockBoundaryAndGuard()
        {
            // P = 50 (p direct); e reaches x in 10 + ex
            Check(Tiny(10, 50, 60, 100).Close.SequenceEqual(new[] { "e" }), "exactly 20 ms over the picked route: closed");
            Check(Tiny(10, 50, 61, 100).Close.Count == 0, "21 ms over: open");
            Check(Tiny(10.4, 50, 60, 100).Close.Count == 0, "20.4 ms over: open");
            Check(Tiny(10, 50, 0, 100).Close.Count == 1, "faster to x than to p: closed");
            // e is the best way to p (10 + 15 = 25), and reaches x in 40: within 25 + 20 -> closed, p direct 50
            var plan = Tiny(10, 50, 30, 15);
            Check(plan.Close.Contains("e") && plan.Unlocked.Ms == 25 && plan.Locked.Ms == 50 && !plan.MayLeak, "closed best entry: 25 -> 50");
            // G5: p doesn't answer, e is the only way and reaches x: kept open, the lock may not hold
            plan = Tiny(10, -1, 30, 15);
            Check(plan.MayLeak && plan.Kept == "e" && plan.Close.Count == 0 && plan.Locked.Ms == 25, "G5 keeps e: " + plan.Locked.Ms);
            // an entry that doesn't answer is never closed (and never a route)
            var net = SdrRoutes.Build(new Dictionary<string, int> { { "e x", 1 }, { "e p", 1 } });
            plan = StrictLock.Plan(net, new Dictionary<string, double> { { "p", 50 } }, new[] { "p" }, new HashSet<string> { "x" }, new HashSet<string> { "x" }, new[] { "e" }, 20);
            Check(plan.Close.Count == 0 && plan.Locked.Ms == 50, "unanswered entry stays as it is");
            // no pings at all: nothing closed, nothing claimed
            plan = StrictLock.Plan(net, new Dictionary<string, double>(), new[] { "p" }, new HashSet<string> { "x" }, new HashSet<string> { "x" }, new[] { "e" }, 20);
            Check(plan.Close.Count == 0 && !plan.MayLeak && !plan.Locked.Known, "no pings: no plan");
            // the saved config never closes every way to a picked region
            var g = SavedDeadlock();
            foreach (var def in g.RegionMap)
            {
                var p = StrictLock.For(g, def.Ids.ToList(), RiyadhPings());
                Check(p.Locked.Known, def.Key + ": a way to the picked region stays open");
            }
            // games without a hosting table (CS2) have no entry relays to close
            var cs2 = SavedDeadlock();
            cs2.HostPops = null;
            Check(StrictLock.For(cs2, new List<string> { "sgp" }, RiyadhPings()).Close.Count == 0, "no hosting table: nothing closed");
        }

        static void CardTellsTheRouteCost()
        {
            Lang.Ar = true;
            var note = new RouteNote { Kind = RouteNoteKind.ClosedEntry, Entry = "دبي", Reaches = "أوروبا", Target = "جنوب شرق آسيا", BeforeMs = 106, AfterMs = 197 };
            var i = Lockable(Choice("SEAsia", "SE Asia", "جنوب شرق آسيا", 106, true));
            i.Route = note;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Choose && v.Sub == "لازم نقفل دبي لأنها توصل لأوروبا. بنقك لجنوب شرق آسيا بيصير تقريباً 197 بدل 106.", "Choose sub: " + v.Sub);
            // the Overwatch zone warning keeps its place
            i.ZoneWarning = "zone";
            Check(CardLogic.Decide(i).Sub == "zone", "zone warning first");
            i.ZoneWarning = null;
            // no number known: the reason alone
            note.AfterMs = -1;
            Check(CardLogic.Decide(i).Sub == "لازم نقفل دبي لأنها توصل لأوروبا.", "no numbers: " + CardLogic.Decide(i).Sub);
            note.AfterMs = 197;
            Lang.Ar = false;
            note.Entry = "Dubai"; note.Reaches = "Europe"; note.Target = "SE Asia";
            Check(CardLogic.Decide(i).Sub == "We have to close Dubai because it reaches Europe. Your ping to SE Asia becomes about 197 instead of 106.", "English sub");
            // locked: only a lock that may not hold says so
            i.LockedNames = "SE Asia";
            v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Locked && v.Sub.StartsWith("Open the game"), "locked, strict: the usual words: " + v.Sub);
            i.Route = new RouteNote { Kind = RouteNoteKind.MayLeak, Entry = "Dubai", Reaches = "Europe", Target = "North America", BeforeMs = 153, AfterMs = 154 };
            v = CardLogic.Decide(i);
            Check(v.Sub.StartsWith("We have to keep Dubai open") && v.Sub.EndsWith("Try a normal match first."), "locked, may leak: " + v.Sub);
            // "للشرق الأوسط": Arabic "to" before al-
            Lang.Ar = true;
            Check(CardLogic.RouteText(new RouteNote { Kind = RouteNoteKind.ClosedEntry, Entry = "دبي", Reaches = "الشرق الأوسط", Target = "أوروبا", BeforeMs = 1, AfterMs = 2 })
                  .Contains("توصل للشرق الأوسط"), "ل + ال");
        }

        static void TileShowsTheRouteAndItsRelay()
        {
            var g = SavedDeadlock();
            var ping = RiyadhPings();
            Func<Region, double> latency = r => StrictLock.Best(g.Routes, new[] { r.Id }, ping, null).Ms;
            Func<Region, string> via = r =>
            {
                var rt = StrictLock.Best(g.Routes, new[] { r.Id }, ping, null);
                return rt.Entry != r.Id && !g.Hosts(g.Regions.First(x => x.Id == rt.Entry)) ? rt.Entry : null;
            };
            var all = RegionGroups.Build(g, null, null, latency, via);
            var sea = all.First(c => c.Def.Key == "SEAsia");
            Check(sea.Ping == 106 && sea.Via == "dxb", "SE Asia tile: 106 via dxb, got " + sea.Ping + " via " + sea.Via);
            var eu = all.First(c => c.Def.Key == "Europe");
            Check(eu.Ping == 58 && eu.Via == "dxb", "Europe tile: 58 via dxb (vie), got " + eu.Ping + " via " + eu.Via);
            // locked to SE Asia: the open set has no dxb/dfjr/bom2 and no other region
            var plan = StrictLock.For(g, RegionIds(g, "SEAsia"), ping);
            Func<string, bool> open = id => !plan.Close.Contains(id) && !plan.Blocked.Contains(id);
            all = RegionGroups.Build(g, null, null, r => StrictLock.Best(g.Routes, new[] { r.Id }, ping, open).Ms, null);
            sea = all.First(c => c.Def.Key == "SEAsia");
            Check(sea.Ping == 197 && sea.Via == null, "locked SE Asia tile: 197 direct, got " + sea.Ping);
            // without a via function nothing is named
            Check(RegionGroups.Build(g, null, null, latency).All(c => c.Via == null), "no via: no name");
        }

        static void RunRouteTests()
        {
            Run("RoutesShortestPaths", RoutesShortestPaths);
            Run("StrictLockEuropeKeepsDubai", StrictLockEuropeKeepsDubai);
            Run("StrictLockSeAsiaClosesDubai", StrictLockSeAsiaClosesDubai);
            Run("StrictLockNorthAmericaFromRiyadh", StrictLockNorthAmericaFromRiyadh);
            Run("StrictLockBoundaryAndGuard", StrictLockBoundaryAndGuard);
            Run("CardTellsTheRouteCost", CardTellsTheRouteCost);
            Run("TileShowsTheRouteAndItsRelay", TileShowsTheRouteAndItsRelay);
        }
    }
}
