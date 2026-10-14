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
        // gateways: the hosting datacenters the strict lock keeps open as entries (LockPlan.Gateways; null = none):
        // not blocked for hosting, only when closed (closedCidrs).
        public static LockTest.LockInfo Plan(Game g, List<Region> picked, string exe, List<string> closedCidrs,
                                             ICollection<string> gateways, Func<IEnumerable<string>, List<string>> resolve)
        {
            // a gateway whose ranges the closed relays block anyway is not kept open
            var closedSet = new HashSet<string>(closedCidrs ?? new List<string>());
            var open = new HashSet<string>((gateways ?? new string[0]).Where(id =>
            {
                var r = g.Regions.FirstOrDefault(x => x.Id == id);
                return r != null && !(r.Cidrs.Count > 0 && r.Cidrs.All(closedSet.Contains));
            }));
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
                block = g.Regions.Where(r => !r.Picked && !r.Detected && g.Hosts(r) && !open.Contains(r.Id)).SelectMany(r => r.Cidrs).Concat(closedCidrs)
                                 .Where(c => !keep.Contains(c)).ToList();
            }
            var info = new LockTest.LockInfo
            {
                Active = true, Picked = picked.Select(r => r.Id).ToList(), Block = block,
                Allow = allow, Gateways = open.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Mode = g.LockSpace != null ? "game-udp" : "valve-all",
                Program = exe, Protocol = exe == null ? null : "UDP",
            };
            info.Ports = info.Protocol != null ? g.LockPorts : null;
            return info;
        }

        // A lock that would add no rule (every region picked): refused, so the card never says "locked" with nothing
        // blocked and the rule check never mistakes the empty rule for one a security app removed.
        public static bool NothingToBlock(LockTest.LockInfo info)
        {
            return info == null || info.Block == null || info.Block.Count == 0;
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

    // Pure: what the lock that is on allows. It belongs to one game (lockGame) and to the datacenters picked when it was
    // applied (LockInfo.Picked): ticking servers afterwards, or another game's old ticks, change neither the firewall
    // nor these answers.
    static class LockScope
    {
        // The picked ids of the lock that is on for g, or null when g isn't the locked game (no lock for it).
        public static HashSet<string> PicksOf(Game lockGame, LockTest.LockInfo info, Game g)
        {
            if (lockGame == null || g == null || g != lockGame || info == null || info.Picked == null) return null;
            return new HashSet<string>(info.Picked);
        }

        // Whether r counts as picked: the lock's own picks while g is locked, the current ticks otherwise.
        public static bool Picked(HashSet<string> picks, Region r)
        {
            return picks != null ? picks.Contains(r.Id) : r.Picked;
        }

        // A row the lock that is on blocks: a hosting datacenter it didn't pick (except a gateway it kept open as an
        // entry), or an entry relay it closed. Only for the locked game (another game has no rule).
        public static bool LockedOut(HashSet<string> picks, ICollection<string> closed, ICollection<string> gateways, Game g, Region r)
        {
            if (picks == null || g == null || !g.CanLock || r == null || r.NoRelay || picks.Contains(r.Id)) return false;
            if (closed != null && closed.Contains(r.Id)) return true;
            return g.Hosts(r) && !(gateways != null && gateways.Contains(r.Id));
        }

        // The lock that is on kept this gateway open as a way in, so a match on it is outside the lock because of the
        // setting (Prefs.DubaiWayIn), not because the lock failed.
        public static bool KeptGateway(LockTest.LockInfo info, string id)
        {
            return info != null && info.Gateways != null && id != null && info.Gateways.Contains(id);
        }

        // Whether switching the Dubai setting (to nowOn) would change the lock that is on: off closes a gateway it kept
        // open; on opens one it blocked only because of the setting (none of its region picked). An SE Asia lock, or one
        // whose R3 closed Dubai anyway, doesn't change.
        public static bool SettingChangesLock(LockTest.LockInfo info, bool nowOn)
        {
            if (info == null || info.Picked == null) return false;
            bool kept = info.Gateways != null && info.Gateways.Count > 0;
            return nowOn ? !kept && DeadlockData.EntryGateways(info.Picked, true).Count > 0 : kept;
        }

        // The gateway the lock that is on picked (Dubai with SE Asia) when the match is somewhere else: that gateway is a
        // way in to other regions too, so the match may have come through it. Null otherwise.
        public static string PickedGateway(Game g, LockTest.LockInfo info, string matchId)
        {
            if (g == null || g.GatewayPops == null || info == null || info.Picked == null) return null;
            return info.Picked.FirstOrDefault(id => g.GatewayPops.Contains(id) && id != matchId);
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
            // the best way in was a gateway (Dubai) the lock blocks: because the player turned its setting off (it would be
            // an entry otherwise), or because the lock picks some of its region by hand and leaves it out ("pick Dubai too")
            if (plan.Blocked.Contains(entry) && costs && g.Gateway(pop(entry)))
            {
                note.Kind = DeadlockData.EntryGateways(plan.Picked, true).Contains(entry) ? RouteNoteKind.GatewayClosed : RouteNoteKind.BlockedHost;
                note.Entry = Places.PlaceName(pop(entry));
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
