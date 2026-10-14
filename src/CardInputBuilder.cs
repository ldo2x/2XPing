using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // What the main window knows right now, as the card needs it (a snapshot taken on the UI thread).
    sealed class CardFacts
    {
        public bool Loaded, LoadFailed, Tested;
        public bool NotAutoTested;            // the automatic test was skipped (the game was open)
        public bool AutoTest;                 // Prefs.AutoTest
        public Game TestGame, TestNext;       // the game being tested, and the one queued after it
        public Game LockingGame;              // a lock for this game is being applied right now
        public string LockingNames;
        public Game LockGame;                 // the locked game; null = no lock
        public string LockNames;              // the lock in the card's words
        public RouteNote LockRoute;           // what the lock that is on costs
        public string MatchRegion, MatchEntry, MatchPlace, CurrentServer;
        public bool MatchOnPicked, MatchViaRelay;
        public bool MatchKeptGateway;         // the match is on a gateway the lock kept open (MatchState.KeptGateway)
        public int CurrentMs = -1;
        public bool NoMatchLong;              // locked a while and no match found yet
        public bool GameLogOn;                // Deadlock's console log is there
        public bool Simple, DevMode;
        public Dictionary<string, int> Activity; // Deadlock matches/hour per matchmaking region; null = none
        public Func<int, int> PlayersOf;
        public Func<List<string>> SeenZones;  // asked only for games with zones
        public Func<List<RegionChoice>, RouteNote> RoutePreview; // Valve: what locking these regions would cost
    }

    // Pure: the facts -> the card's input (what CardLogic.Decide reads).
    static class CardInputBuilder
    {
        // Games that can't be locked: the lowest ping among the game's own servers (not the ones found in a match).
        public static Region BestOwn(Game g)
        {
            return Routing.Best(g.Regions.Where(r => !r.Detected));
        }

        public static CardInput Build(Game g, List<RegionChoice> every, CardFacts f)
        {
            bool locked = f.LockGame != null && f.LockGame == g;
            var input = new CardInput
            {
                Game = g.Name,
                Loaded = f.Loaded,
                LoadFailed = f.LoadFailed,
                // this game's test runs, or it is next in line after another game's
                Testing = f.TestGame == g || (f.TestGame != null && !f.Tested && (f.AutoTest || f.TestNext == g)),
                Tested = f.Tested,
                CanLock = g.CanLock,
                Locking = f.LockingGame == g,
                LockingNames = f.LockingGame == g ? f.LockingNames : null,
                InDevelopment = g.InDevelopment,
                // every region, shown or folded away: the button names and locks all the selected ones
                Choices = every,
                LockedNames = locked ? f.LockNames : null,
                MatchRegion = f.MatchRegion,
                MatchOnPicked = f.MatchOnPicked,
                MatchViaRelay = f.MatchViaRelay,
                MatchEntry = f.MatchEntry,
                MatchPlace = f.MatchPlace,
                MatchKeptGateway = locked && f.MatchKeptGateway,
                // Deadlock without its console log: how to switch it on (a link in the simple layout, the steps in the advanced one)
                Hint = !DeadlockData.Is(g) || f.GameLogOn ? ExactHint.None : f.Simple && !f.DevMode ? ExactHint.Link : ExactHint.Shown,
                CurrentServer = f.CurrentServer,
                CurrentMs = f.CurrentMs,
                NoMatchYet = f.NoMatchLong && locked,
                RussiaNote = g.AppId == "1422450",
                OldPings = f.NotAutoTested && !f.Tested
            };
            int russia;
            if (input.RussiaNote && f.Activity != null && f.Activity.TryGetValue(DeadlockData.Russia, out russia)) input.RussiaPlayers = f.PlayersOf(russia);
            // Overwatch: the picked regions are all outside the game regions the player's matches were on
            if (g.CanLock && Zones.Has(g)) input.ZoneWarning = Zones.Warning(RegionGroups.Chosen(every), f.SeenZones());
            // Valve: what the strict lock on the chosen regions costs, before it is applied; the lock that is on keeps its own
            if (g.CanLock && g.Provider == Provider.Valve && g.Routes != null)
            {
                if (input.LockedNames != null) input.Route = f.LockRoute;
                else
                {
                    var chosen = RegionGroups.Chosen(every);
                    if (chosen.Count > 0) input.Route = f.RoutePreview(chosen);
                }
            }
            if (!g.CanLock)
            {
                var best = BestOwn(g);
                if (best != null)
                {
                    input.BestServer = Fmt.ShortName(best);
                    input.BestMs = (int)Math.Round(Routing.BestLatency(best));
                }
            }
            return input;
        }
    }
}
