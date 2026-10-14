using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PingBoost
{
    // Deadlock honest detection (audit I4, B3, B4, G2): the >=50% rule, the console log, the relay-only card.
    static partial class Tests
    {
        static string RelayIp(Game g, string id)
        {
            var r = g.Regions.First(x => x.Id == id);
            return r.Cidrs.First().Split('/')[0];
        }

        static Detector.Hit HitOf(string ip, int packets) { return new Detector.Hit { Ip = ip, Port = 27015, Packets = packets }; }

        static void ShareRuleCountsEveryPacket()
        {
            // 300 game packets in all: 120 dxb, 90 fra, 80 ams, 10 small ones that never make the list
            var all = new[] { HitOf("1.1.1.1", 120), HitOf("2.2.2.2", 90), HitOf("3.3.3.3", 80), HitOf("4.4.4.4", 6), HitOf("5.5.5.5", 4) };
            var top = Detector.Top(all);
            Check(top.Count == 3 && top[0].Ip == "1.1.1.1", "busiest first, under 20 packets left out: " + top.Count);
            Check(Math.Abs(top[0].Share - 0.4) < 1e-9, "share counts the small ones too: " + top[0].Share);
            Check(!top.Any(Detector.CarriesMatch), "40% carries nothing");
            var one = Detector.Top(new[] { HitOf("1.1.1.1", 150), HitOf("2.2.2.2", 150) });
            Check(one.All(Detector.CarriesMatch), "exactly half carries the match");
            var less = Detector.Top(new[] { HitOf("1.1.1.1", 149), HitOf("2.2.2.2", 151) });
            Check(!Detector.CarriesMatch(less.First(h => h.Ip == "1.1.1.1")) && Detector.CarriesMatch(less.First(h => h.Ip == "2.2.2.2")),
                  "149/300 doesn't, 151/300 does");
            Check(Detector.Top(new Detector.Hit[0]).Count == 0, "nothing captured");
        }

        static void HostNeedsHalfThePackets()
        {
            var g = SavedDeadlock();
            string dxb = RelayIp(g, "dxb"), fra = RelayIp(g, "fra");
            // the audit's case: Dubai carries the game, Frankfurt is the backup router kept alive (B3)
            var hits = Detector.Top(new[] { HitOf(dxb, 950), HitOf(fra, 50) });
            Check(MatchClassifier.HostRegionOf(g, hits) == null, "a 5% Frankfurt is never the match");
            var relay = MatchClassifier.RelayRegionOf(g, hits);
            Check(relay != null && relay.Id == "dxb", "entering at Dubai: " + (relay != null ? relay.Id : "none"));
            // Frankfurt carries most of it: that's the match
            hits = Detector.Top(new[] { HitOf(dxb, 100), HitOf(fra, 900) });
            var dc = MatchClassifier.HostRegionOf(g, hits);
            Check(dc != null && dc.Id == "fra", "90% Frankfurt is the match");
            // 45% Frankfurt, 40% Dubai, the rest elsewhere: no match, the busiest Valve address is the way in
            hits = Detector.Top(new[] { HitOf(fra, 450), HitOf(dxb, 400), HitOf("8.8.8.8", 150) });
            Check(MatchClassifier.HostRegionOf(g, hits) == null, "45% is not the match");
            relay = MatchClassifier.RelayRegionOf(g, hits);
            Check(relay != null && relay.Id == "fra", "entering at Frankfurt (used as a relay): " + (relay != null ? relay.Id : "none"));
            // nothing of Valve's
            hits = Detector.Top(new[] { HitOf("8.8.8.8", 500) });
            Check(MatchClassifier.HostRegionOf(g, hits) == null && MatchClassifier.RelayRegionOf(g, hits) == null, "no Valve address, nothing");
        }

        // The game's real lines (format strings from Deadlock's steamnetworkingsockets.dll, audit §4).
        static void ConsoleParsesTheGamesLines()
        {
            var now = new DateTime(2026, 10, 13, 22, 0, 0);
            string kind;
            var l = GameConsole.Parse("    Remote host is in data center 'fra'", now, out kind);
            Check(l != null && kind == "dc" && l.Code == "fra" && l.At == DateTime.MaxValue, "plain dc line");
            l = GameConsole.Parse("10/13 21:33:12 [SteamNetSockets]     Remote host is in data center 'vie'", now, out kind);
            Check(l != null && l.Code == "vie" && l.At == new DateTime(2026, 10, 13, 21, 33, 12), "dated dc line: " + (l != null ? l.At.ToString("s") : "null"));
            l = GameConsole.Parse("Communicating via relay in 'dxb'", now, out kind);
            Check(l != null && kind == "relay" && l.Code == "dxb", "relay line");
            l = GameConsole.Parse("    Remote host is in data center 'sto2'", now, out kind);
            Check(l != null && l.Code == "sto2", "a POP id with a number");
            l = GameConsole.Parse("Remote host is in data center 'FRA#12 (155.133.226.68:27015)'", now, out kind);
            Check(l != null && l.Code == "fra", "only the POP id, lowercase: " + (l != null ? l.Code : "null"));
            Check(GameConsole.Parse("Primary router: dxb#3 (185.25.183.163:27015)  Ping = 31+28=59 (front+back=total)", now, out kind) == null,
                  "other lines are not a datacenter");
            Check(GameConsole.Parse("Remote host is in data center ''", now, out kind) == null, "an empty name is nothing");
            Check(GameConsole.Parse("Ping location: dxb=31+3,fra=72+7/69+5", now, out kind) == null, "the ping location is nothing");
            // the lines a real match wrote (2026-10-14, Frankfurt from Riyadh): the relays next to the server name its place
            l = GameConsole.Parse("10/14 12:08:26 [SteamNetSockets] [#1671292347 SDR server steamid:90294866394211354 vport 0] Requesting session from fra#328 (155.133.226.87:27020).  Ping = 74 = 73+1 (front+back).  Rank=1 Score=99", now, out kind);
            Check(l != null && kind == "dc" && l.Code == "fra", "session relay 1 ms from the server: its datacenter");
            l = GameConsole.Parse("10/14 12:08:26 [SteamNetSockets] [#1671292347 SDR server steamid:90294866394211354 vport 0] Requesting session from dfra#51 (89.222.108.162:27019).  Ping = 78 = 78+0 (front+back).  Rank=2 Score=103", now, out kind);
            Check(l != null && kind == "dc" && l.Code == "dfra", "session relay 0 ms from the server");
            Check(GameConsole.Parse("[#1 SDR server steamid:1 vport 0] Requesting session from dxb#3 (185.25.183.163:27020).  Ping = 98 = 58+40 (front+back).  Rank=1 Score=99", now, out kind) == null,
                  "a far entry relay names no place");
            l = GameConsole.Parse("10/14 12:08:26 [SteamNetSockets] [#1671292347 SDR server steamid:90294866394211354 vport 0] Selecting fra#328 (155.133.226.87:27020) as primary.  (Ping = 74 = 73+1+0 (front+interior+remote).  Rank=1 Score=81)", now, out kind);
            Check(l != null && kind == "relay" && l.Code == "fra", "the primary relay is the way in");
            // "12/31" read on 1 January was last year
            l = GameConsole.Parse("12/31 23:59:00 Remote host is in data center 'ams'", new DateTime(2027, 1, 1, 0, 5, 0), out kind);
            Check(l != null && l.At.Year == 2026, "last year's date: " + (l != null ? l.At.ToString("s") : "null"));
            // known ids: itself, or without the number (tyo1 -> tyo)
            Func<string, bool> known = c => DeadlockData.IsKnown(c);
            Check(GameConsole.Known("sto2", known) == "sto2" && GameConsole.Known("tyo1", known) == "tyo" && GameConsole.Known("xyz9", known) == null,
                  "known ids");
            Check(GameConsole.PathFromExe(null) == null, "no exe, no path");
        }

        static string TempLog()
        {
            string dir = Path.Combine(Path.GetTempPath(), "2xping-tests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "console-" + Guid.NewGuid().ToString("N") + ".log");
        }

        static void Append(string path, string text) { File.AppendAllText(path, text, new UTF8Encoding(false)); }

        static void ConsoleReadsOnlyNewLines()
        {
            string path = TempLog();
            try
            {
                var t0 = new DateTime(2026, 10, 13, 20, 0, 0);
                // a log from an earlier session: undated lines of unknown age never count
                File.WriteAllText(path, "Ping location: dxb=31\n    Remote host is in data center 'fra'\n", new UTF8Encoding(false));
                var log = new GameConsole(path);
                Check(log.Read(t0), "reads");
                Check(log.LastDc != null && log.LastDc.Code == "fra" && log.LastDc.At == DateTime.MinValue, "old line kept as old");
                Check(GameConsole.Current(log.LastDc, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue) == null, "an old line is never fresh");
                // a new match: the line appears after the first read, stamped with the read time
                var t1 = t0.AddMinutes(5);
                Append(path, "Communicating via relay in 'dxb'\n    Remote host is in data cen");
                log.Read(t1);
                Check(log.LastDc.Code == "fra" && log.LastRelay != null && log.LastRelay.Code == "dxb", "half a line waits");
                Append(path, "ter 'vie'\n");
                var t2 = t1.AddSeconds(30);
                log.Read(t2);
                Check(log.LastDc.Code == "vie" && log.LastDc.At == t2, "the finished line counts: " + log.LastDc.Code);
                Check(GameConsole.Current(log.LastDc, t0, DateTime.MinValue, DateTime.MinValue) != null, "written after the cutoff: fresh");
                Check(GameConsole.Current(log.LastDc, t0, DateTime.MinValue, t2) == null, "read in a check that saw no match: stale");
                // nothing new: nothing changes
                log.Read(t2.AddMinutes(1));
                Check(log.LastDc.Code == "vie" && log.LastDc.At == t2, "no new bytes, same answer");

                // the game cleared the log (shorter now): read from its start, every line is new
                File.WriteAllText(path, "    Remote host is in data center 'ams'\n", new UTF8Encoding(false));
                var t3 = t2.AddMinutes(10);
                log.Read(t3);
                Check(log.LastDc.Code == "ams" && log.LastDc.At == t3, "a shorter file is a new log: " + log.LastDc.Code);
                // replaced by a longer one that starts differently: also a new log
                File.WriteAllText(path, "Different start of a new session, quite a bit longer than before ...........\n" +
                                        "    Remote host is in data center 'waw'\n", new UTF8Encoding(false));
                var t4 = t3.AddMinutes(10);
                log.Read(t4);
                Check(log.LastDc.Code == "waw" && log.LastDc.At == t4, "a different start is a new log: " + log.LastDc.Code);

                // dated lines carry their own time, even in the first read
                File.WriteAllText(path, "10/13 19:00:00 Remote host is in data center 'lhr'\n10/13 21:40:00 Remote host is in data center 'par'\n",
                                  new UTF8Encoding(false));
                var fresh = new GameConsole(path);
                fresh.Read(new DateTime(2026, 10, 13, 21, 45, 0));
                Check(fresh.LastDc.Code == "par" && fresh.LastDc.At == new DateTime(2026, 10, 13, 21, 40, 0), "the newest dated line");
                Check(GameConsole.Current(fresh.LastDc, new DateTime(2026, 10, 13, 21, 30, 0), DateTime.MinValue, DateTime.MinValue) != null, "after the game started: fresh");
                Check(GameConsole.Current(fresh.LastDc, new DateTime(2026, 10, 13, 21, 41, 0), DateTime.MinValue, DateTime.MinValue) == null, "before the game started: stale");

                // the log is gone: nothing current
                File.Delete(path);
                Check(!fresh.Read(DateTime.Now) && fresh.LastDc == null, "no log, no answer");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        static void ConsoleFirstReadIsTheTailOnly()
        {
            string path = TempLog();
            try
            {
                var sb = new StringBuilder();
                sb.Append("10/13 10:00:00 Remote host is in data center 'sgp'\n");
                string filler = new string('x', 200) + "\n";
                while (sb.Length < GameConsole.FirstReadMax + 4096) sb.Append(filler);
                sb.Append("10/13 11:00:00 Remote host is in data center 'hkg'\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                var log = new GameConsole(path);
                log.Read(new DateTime(2026, 10, 13, 12, 0, 0));
                Check(log.LastDc != null && log.LastDc.Code == "hkg", "the newest line, from the last MB");
                // the sgp line is beyond the first read: a big log is never read whole
                Append(path, "nothing\n");
                log.Read(new DateTime(2026, 10, 13, 12, 1, 0));
                Check(log.LastDc.Code == "hkg", "the next read takes only the new bytes");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        static void ConsoleOnPickedByRegion()
        {
            var g = SavedDeadlock();
            var lockable = g.Regions.Where(r => g.Hosts(r)).Select(r => r.Id).ToList();
            var europe = lockable.Where(id => DeadlockData.Hosting[id] == "Europe").ToList();
            Check(europe.Contains("fra") && europe.Contains("ams") && !europe.Contains("hel"), "Europe's lockable datacenters");
            // a datacenter with relays: its own pick
            Check(DeadlockData.OnPicked("fra", true, new[] { "fra" }, lockable), "picked itself");
            Check(!DeadlockData.OnPicked("ams", false, new[] { "fra" }, lockable), "a datacenter with relays, not picked");
            // Helsinki (no relay, can't be blocked): only with the whole of Europe locked
            Check(DeadlockData.OnPicked("hel", null, europe, lockable), "Helsinki with all of Europe locked");
            Check(!DeadlockData.OnPicked("hel", null, new[] { "fra", "ams" }, lockable), "Helsinki with only Frankfurt and Amsterdam: outside");
            Check(!DeadlockData.OnPicked("hel", null, europe.Where(id => id != "jnb").ToList(), lockable), "Europe without Johannesburg: outside");
            Check(!DeadlockData.OnPicked("hel", null, new[] { "sgp" }, lockable), "Helsinki with an SE Asia lock");
            Check(DeadlockData.OnPicked("dsto", null, europe, lockable) && DeadlockData.OnPicked("fsn", null, europe, lockable), "dsto, fsn with Europe");
            var na = lockable.Where(id => DeadlockData.Hosting[id] == "Row").ToList();
            Check(DeadlockData.OnPicked("eat", null, na, lockable) && !DeadlockData.OnPicked("eat", null, na.Skip(1), lockable), "US West needs all of North America");
            Check(!DeadlockData.OnPicked("dxb", null, new[] { "fra" }, lockable), "a relay is never a match");
            Check(!DeadlockData.OnPicked("eat", null, new string[0], lockable), "nothing picked");
            Check(!DeadlockData.OnPicked("eat", null, new string[0], new string[0]), "a region with nothing lockable is never picked");
        }

        // When a console line is about the current match (GameConsole.Current): game start, lock change, no-match check.
        static void ConsoleCurrentMatch()
        {
            var start = new DateTime(2026, 10, 13, 20, 0, 0);
            Func<DateTime, ConsoleLine> at = t => new ConsoleLine { Code = "fra", At = t };
            var none = DateTime.MinValue;
            Check(GameConsole.Current(null, none, none, none) == null, "no line");
            Check(GameConsole.Current(at(DateTime.MinValue), none, none, none) == null, "a line of unknown age never counts");
            Check(GameConsole.Current(at(start.AddMinutes(1)), start, none, none) != null, "after the game started");
            Check(GameConsole.Current(at(start.AddMinutes(-1)), start, none, none) == null, "from the game's earlier run");
            Check(GameConsole.Current(at(start.AddMinutes(5)), start, start.AddMinutes(6), none) == null, "before the lock changed");
            Check(GameConsole.Current(at(start.AddMinutes(7)), start, start.AddMinutes(6), none) != null, "after the lock changed");
            // match 1: its line is read by the 20 s follower at 20:10 while 2X Ping is behind the game
            var m1 = at(start.AddMinutes(10));
            // back in the menu: a check at 20:30 sees no match and reads the log then (no new line): match 1's line is stale
            var menu = start.AddMinutes(30);
            Check(GameConsole.Current(m1, start, none, none) != null, "match 1, before any no-match check");
            Check(GameConsole.Current(m1, start, none, menu) == null, "match 1 read in the menu check: stale");
            // a line read in that very no-match check (stamped with the check's read time) is stale too
            Check(GameConsole.Current(at(menu), start, none, menu) == null, "read in the no-match check itself: stale");
            // match 2 prints its line, read at 20:40: current
            Check(GameConsole.Current(at(start.AddMinutes(40)), start, none, menu) != null, "match 2 after the menu: current");
        }

        static void ConsoleLaterReadCap()
        {
            string path = TempLog();
            try
            {
                File.WriteAllText(path, "start\n", new UTF8Encoding(false));
                var log = new GameConsole(path);
                log.Read(new DateTime(2026, 10, 13, 20, 0, 0));
                // more than 4 MB written between two reads: an old datacenter early, then filler, then the newest
                var sb = new StringBuilder();
                sb.Append("    Remote host is in data center 'sgp'\n");
                string filler = new string('y', 300) + "\n";
                while (sb.Length < GameConsole.ReadMax + 8192) sb.Append(filler);
                Append(path, sb.ToString());
                var t = new DateTime(2026, 10, 13, 20, 1, 0);
                log.Read(t);
                Check(log.LastDc == null, "the line beyond the 4 MB cap is not read (only the newest 4 MB)");
                Append(path, "    Remote host is in data center 'hkg'\n");
                log.Read(t.AddSeconds(20));
                Check(log.LastDc != null && log.LastDc.Code == "hkg" && log.LastDc.At == t.AddSeconds(20), "reading goes on after the cap");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        // A new session that rewrote the log with the same first bytes and grew past the old end is still a new log.
        static void ConsoleRewrittenSameStart()
        {
            string path = TempLog();
            try
            {
                string header = "Steam Console Client (c) Valve Corporation - version 1.0 - common header line\n";
                File.WriteAllText(path, header + "old session line one\n    Remote host is in data center 'lhr'\n", new UTF8Encoding(false));
                var log = new GameConsole(path);
                log.Read(new DateTime(2026, 10, 13, 20, 0, 0));
                Check(log.LastDc.Code == "lhr" && log.LastDc.At == DateTime.MinValue, "first read: old");
                File.WriteAllText(path, header + "new session, a different line here\n    Remote host is in data center 'par'\nmore lines after it\n",
                                  new UTF8Encoding(false));
                var t = new DateTime(2026, 10, 13, 20, 5, 0);
                log.Read(t);
                Check(log.LastDc.Code == "par" && log.LastDc.At == t, "rewritten under the old end: read from the start: " + log.LastDc.Code);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        static void CardRelayOnlyIsNeutral()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchViaRelay = true; i.MatchEntry = "دبي";
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.InMatchRelay, "relay-only kind");
            Check(v.Title == "داخل شبكة Valve من دبي، وسيرفر المباراة ما يبان من هنا", "title: " + v.Title);
            Check(!v.Title.Contains("✓") && !v.Sub.Contains("✓") && !v.Sub.Contains("شغّال"), "no ✓, no 'working'");
            Check(v.Sub == "مثبّت على أوروبا. وبنقك الحقيقي تشوفه داخل اللعبة.", "sub: " + v.Sub);
            Check(v.Accent == Theme.Accent && v.Button == "فك التثبيت", "neutral accent, unlock");
            Check(v.HintLink == null && v.HintSteps == null, "no hint without one asked for");
            i.MatchEntry = null;
            Check(CardLogic.Decide(i).Title == "داخل شبكة Valve من وسيط، وسيرفر المباراة ما يبان من هنا", "unknown entry");
            Lang.Ar = false;
            i.LockedNames = "Europe"; i.MatchEntry = "Dubai";
            v = CardLogic.Decide(i);
            Check(v.Title == "Entering Valve's network at Dubai; the match server can't be seen from here", "english title: " + v.Title);
            Check(v.Sub == "Locked to Europe. The ping shown in the game is the real one.", "english sub: " + v.Sub);
            Lang.Ar = true;
        }

        static void CardExactHint()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchViaRelay = true; i.MatchEntry = "دبي";
            i.Hint = ExactHint.Link;
            var v = CardLogic.Decide(i);
            Check(v.HintLink == "تبغى نعرف السيرفر بالضبط؟", "simple: the link: " + v.HintLink);
            Check(v.HintSteps != null && v.HintSteps.Contains("-condebug") && v.HintSteps.Contains("خيارات التشغيل"), "its steps: " + v.HintSteps);
            // simple: only in the relay-only state
            var locked = LockedInput(); locked.Hint = ExactHint.Link;
            v = CardLogic.Decide(locked);
            Check(v.HintLink == null && v.HintSteps == null, "simple, locked: no hint");
            var ok = LockedInput(); ok.Hint = ExactHint.Link; ok.MatchRegion = "أوروبا"; ok.MatchOnPicked = true;
            Check(CardLogic.Decide(ok).HintSteps == null, "simple, in a seen match: no hint");
            // advanced and developer mode: the steps on every locked state, no link
            locked.Hint = ExactHint.Shown;
            v = CardLogic.Decide(locked);
            Check(v.HintLink == null && v.HintSteps != null, "advanced: the steps themselves");
            i.Hint = ExactHint.Shown;
            Check(CardLogic.Decide(i).HintSteps != null, "advanced, relay-only: the steps");
            // never on the choose screen
            var choose = Lockable(Choice("eu", "Europe", "أوروبا", 78, true));
            choose.Hint = ExactHint.Shown;
            Check(CardLogic.Decide(choose).HintSteps == null, "choose: no hint");
            Lang.Ar = false;
            i.Hint = ExactHint.Link;
            v = CardLogic.Decide(i);
            Check(v.HintLink == "Want to know the exact server?", "english link: " + v.HintLink);
            Check(v.HintSteps == "In Steam: Deadlock → Properties → Launch options, add -condebug, then close the game and open it again. " +
                                 "After that we can name the exact server.", "english steps: " + v.HintSteps);
            Lang.Ar = true;
        }

        static void CardExactPlaceFromLog()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchRegion = "أوروبا"; i.MatchOnPicked = true; i.MatchPlace = "فرانكفورت";
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.InMatchOk && v.Title == "تلعب دحين في أوروبا (فرانكفورت) ✓", "on picked, exact: " + v.Title);
            i.MatchRegion = "جنوب شرق آسيا"; i.MatchOnPicked = false; i.MatchPlace = "سنغافورة";
            v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Outside && v.Title == "اللعبة حطّتك على جنوب شرق آسيا (سنغافورة)", "outside, exact: " + v.Title);
            // the log's answer wins over a relay seen in the capture
            i.MatchViaRelay = true;
            Check(CardLogic.Decide(i).Kind == CardKind.Outside, "a known match beats the relay");
            Lang.Ar = false;
            i.MatchRegion = "Europe"; i.MatchOnPicked = true; i.MatchPlace = "Frankfurt"; i.MatchViaRelay = false; i.LockedNames = "Europe";
            Check(CardLogic.Decide(i).Title == "You're playing in Europe (Frankfurt) now ✓", "english exact");
            Lang.Ar = true;
        }

        static void RunDetectionTests()
        {
            Run("ShareRuleCountsEveryPacket", ShareRuleCountsEveryPacket);
            Run("HostNeedsHalfThePackets", HostNeedsHalfThePackets);
            Run("ConsoleParsesTheGamesLines", ConsoleParsesTheGamesLines);
            Run("ConsoleReadsOnlyNewLines", ConsoleReadsOnlyNewLines);
            Run("ConsoleFirstReadIsTheTailOnly", ConsoleFirstReadIsTheTailOnly);
            Run("ConsoleOnPickedByRegion", ConsoleOnPickedByRegion);
            Run("ConsoleCurrentMatch", ConsoleCurrentMatch);
            Run("ConsoleLaterReadCap", ConsoleLaterReadCap);
            Run("ConsoleRewrittenSameStart", ConsoleRewrittenSameStart);
            Run("CardRelayOnlyIsNeutral", CardRelayOnlyIsNeutral);
            Run("CardExactHint", CardExactHint);
            Run("CardExactPlaceFromLog", CardExactPlaceFromLog);
        }
    }
}
