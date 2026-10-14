using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // Pure: what a lock blocks and how it is described. LockServers applies it (ServerLock on the FirewallWorker).
    static class LockPlanner
    {
        // The address ranges of the Valve entry relays the strict lock closes too.
        public static List<string> ClosedCidrs(Game g, ICollection<string> closed)
        {
            return g.Regions.Where(r => closed.Contains(r.Id)).SelectMany(r => r.Cidrs).ToList();
        }

        // The lock on the picked datacenters. resolve: IpLists.Resolve (named lists -> address ranges).
        // exe: games locked by "everything except the picked datacenters" block only their own UDP (null = whole PC).
        public static LockTest.LockInfo Plan(Game g, List<Region> picked, string exe, List<string> closedCidrs,
                                             Func<IEnumerable<string>, List<string>> resolve)
        {
            List<string> block;
            var allow = resolve(g.LockAllow ?? new string[0]);
            if (g.LockSpace != null)
                // everything the game's servers can live on, except the picked datacenters (new servers included)
                block = IpLists.Subtract(resolve(g.LockSpace), picked.SelectMany(r => r.Cidrs).Concat(allow));
            else
            {
                var keep = new HashSet<string>(picked.SelectMany(r => r.Cidrs));
                // the other regions' datacenters, and the entry relays the strict lock closes; the other relays
                // stay open: the game enters Valve's network through them
                block = g.Regions.Where(r => !r.Picked && !r.Detected && g.Hosts(r)).SelectMany(r => r.Cidrs).Concat(closedCidrs)
                                 .Where(c => !keep.Contains(c)).ToList();
            }
            var info = new LockTest.LockInfo
            {
                Active = true, Picked = picked.Select(r => r.Id).ToList(), Block = block,
                Allow = allow,
                Mode = g.LockSpace != null ? "game-udp" : "valve-all",
                Program = exe, Protocol = exe == null ? null : "UDP",
            };
            info.Ports = info.Protocol != null ? g.LockPorts : null;
            return info;
        }

        // The rule's scope in words (developer tools, English only). n: the ranges the rule holds.
        public static string Scope(LockTest.LockInfo info, int n)
        {
            return (info.Protocol ?? "any protocol") + ", in+out, " +
                   (info.Program != null ? "program " + info.Program : "whole PC (every program)") + ", " +
                   (info.Ports != null ? "remote ports " + info.Ports : "all ports") + ", " + n + " ranges";
        }

        // How the card names a lock: the regions when whole regions are picked, otherwise the picked servers.
        // choices: the game's region tiles (MainForm.BuildChoices).
        public static string CardNames(Game g, List<Region> picked, List<RegionChoice> choices)
        {
            var ids = new HashSet<string>(picked.Where(r => g.Hosts(r)).Select(r => r.Id));
            var whole = choices.Where(c => c.Members.All(m => ids.Contains(m.Id))).ToList();
            var covered = new HashSet<string>(whole.SelectMany(c => c.Members).Select(m => m.Id));
            if (whole.Count > 0 && ids.All(covered.Contains)) return CardLogic.JoinNames(whole.Select(c => c.Name).ToList());
            return string.Join(Lang.Ar ? "، " : ", ", picked.Select(Fmt.ShortName).ToArray());
        }
    }

    // Pure: what a strict lock plan (StrictLock) costs, in the card's terms.
    static class RouteAdvisor
    {
        // Told when it costs this much more (or leaves no measured way in)
        public const int WorthTellingMs = 10;

        // null = nothing worth telling. The entry relay the lock closes was the best way in (R3), the best way in is a
        // datacenter of a region not picked (I3, 10 ms or more), or an entry that reaches a left-out region has to stay
        // open (G5). target: the picked regions' names.
        public static RouteNote NoteOf(Game g, LockPlan plan, string target)
        {
            if (plan == null) return null;
            var note = new RouteNote
            {
                Target = target,
                BeforeMs = plan.Unlocked.Known ? (int)Math.Round(plan.Unlocked.Ms) : -1,
                AfterMs = plan.Locked.Known ? (int)Math.Round(plan.Locked.Ms) : -1,
            };
            Func<string, Region> pop = id => g.Regions.FirstOrDefault(r => r.Id == id);
            Route why;
            if (plan.MayLeak)
            {
                note.Kind = RouteNoteKind.MayLeak;
                note.Entry = Places.PlaceName(pop(plan.Kept));
                note.Reaches = plan.Reason.TryGetValue(plan.Kept, out why) ? Places.RegionOfPop(g, why.To) : null;
                return note;
            }
            string entry = plan.Unlocked.Entry;
            if (entry == null) return null;
            bool costs = !plan.Locked.Known || plan.Locked.Ms - plan.Unlocked.Ms >= WorthTellingMs;
            // the closed entry was the best way in
            if (plan.Close.Contains(entry) && costs)
            {
                note.Kind = RouteNoteKind.ClosedEntry;
                note.Entry = Places.PlaceName(pop(entry));
                note.Reaches = Places.RegionOfPop(g, plan.Reason[entry].To);
                return note;
            }
            if (plan.Blocked.Contains(entry) && costs)
            {
                note.Kind = RouteNoteKind.BlockedHost;
                note.Entry = Places.RegionOfPop(g, entry);
                return note;
            }
            return null;
        }
    }
}
