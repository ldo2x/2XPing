using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PingBoost
{
    // Player numbers at one moment (immutable once fetched).
    sealed class PlayerStats
    {
        public static readonly PlayerStats None = new PlayerStats(null, -1, -1);

        public readonly Dictionary<string, int> Activity; // Deadlock matches/hour per matchmaking region; null = none
        public readonly int Online;              // Deadlock players online (in matches, the queue, the menus or away); -1 = unknown
        public readonly int Steam;               // Steam's player count of the game; -1 = unknown

        public PlayerStats(Dictionary<string, int> activity, int online, int steam)
        {
            Activity = activity;
            Online = online;
            Steam = steam;
        }

        // A region's players: the online total split by the region's share of the live matches.
        // Without the total, the old estimate from the live-match sample (far below the real number).
        public int PlayersOf(int matches)
        {
            int all = Activity != null ? Activity.Values.Sum() : 0;
            if (Online > 0 && all > 0) return (int)Math.Round((double)Online * matches / all);
            return Stats.EstimatePlayers(matches);
        }

        // Deadlock: servers are grouped under their matchmaking region, busiest region first.
        public bool Grouped(Game g) { return g != null && g.AppId == "1422450" && Activity != null && Activity.Count > 0; }

        // The matchmaking region a Deadlock datacenter's row sits under ("" = none with numbers)
        public string RegionKey(Region r)
        {
            string mm;
            return DeadlockData.Hosting.TryGetValue(r.Id, out mm) && Activity != null && Activity.ContainsKey(mm) ? mm : "";
        }

        // The info bar's line about these numbers.
        public string Summary(Game g)
        {
            if (Activity != null && Activity.Count > 0)
            {
                int total = Activity.Values.Sum(m => PlayersOf(m));
                return Lang.T("Online now (est.): ", "أونلاين دحين تقريباً: ") + Stats.Approx(total) + Lang.T("", " لاعب") + "   |   " +
                       string.Join("   ", Activity.OrderByDescending(kv => kv.Value).Take(4).Select(kv =>
                           (Stats.MmRegions.ContainsKey(kv.Key) ? Lang.T(Stats.MmRegions[kv.Key][0], Stats.MmRegions[kv.Key][1]) : kv.Key) +
                           " " + Stats.Approx(PlayersOf(kv.Value))));
            }
            if (Steam >= 0)
                return Lang.T("Online now on Steam: ", "أونلاين دحين على Steam: ") + Steam.ToString("N0") +
                       Lang.T(" players (all regions together)", " لاعب (كل المناطق مع بعض)");
            if (g.Provider == Provider.Custom) return "";
            return Lang.T("Player numbers aren't available for this game.", "عدد اللاعبين مو متوفر لهادي اللعبة.");
        }

        // The real sources (deadlock-api through Supabase, Steam); runs on a background thread.
        public static PlayerStats Fetch(Game g)
        {
            Dictionary<string, int> activity = null;
            int online = -1;
            if (g.AppId == "1422450")
            {
                try { activity = Stats.DeadlockActivity(); } catch { }
                try { online = Stats.DeadlockOnline(); } catch { }
            }
            return new PlayerStats(activity, online, Stats.SteamPlayers(g.SteamAppId));
        }
    }

    // The player numbers of the game on screen: fetched in the background, kept only while that game is still on screen.
    sealed class StatsService
    {
        readonly IUiDispatcher ui;
        readonly Func<Game, PlayerStats> fetch;
        readonly Func<Game> current;
        readonly Action<Action> background;
        readonly Func<DateTime> now;

        public StatsService(IUiDispatcher ui, Func<Game, PlayerStats> fetch, Func<Game> current)
            : this(ui, fetch, current, work => Task.Run(work), () => DateTime.Now) { }

        // tests: background = run at once, now = a fake clock
        public StatsService(IUiDispatcher ui, Func<Game, PlayerStats> fetch, Func<Game> current, Action<Action> background, Func<DateTime> now)
        {
            this.ui = ui;
            this.fetch = fetch;
            this.current = current;
            this.background = background;
            this.now = now;
            Current = PlayerStats.None;
            At = DateTime.MinValue;
        }

        public PlayerStats Current { get; private set; } // the numbers on screen, never null
        public DateTime At { get; private set; }          // when the last fetch started

        // On the UI thread, for the game on screen (the numbers are dropped if another game is on screen by then).
        public event Action<Game, PlayerStats, bool> Updated; // quiet: the list keeps its scroll position

        public bool Stale(TimeSpan fresh) { return now() - At >= fresh; }

        public void Load(Game g, bool quiet)
        {
            At = now();
            background(() =>
            {
                var s = fetch(g) ?? PlayerStats.None;
                ui.Post(() =>
                {
                    if (current() != g) return;
                    Current = s;
                    var h = Updated;
                    if (h != null) h(g, s, quiet);
                });
            });
        }
    }
}
