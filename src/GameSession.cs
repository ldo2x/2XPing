using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PingBoost
{
    // What this session knows about each game: its servers loaded (or failed), tested, or its automatic test skipped;
    // and the server loading itself (in the background, results on the UI thread for the game still on screen).
    sealed class GameSession
    {
        readonly IUiDispatcher ui;
        readonly Action<Game> load;          // GameDb.Load: throws when the servers can't be had
        readonly Func<Game, bool> gameOpen;  // is the game running (asked only with the automatic test on)
        readonly Func<Game> current;         // the game on screen
        readonly Action<Action> background;
        readonly Action<int> sleep;

        readonly HashSet<Game> tested = new HashSet<Game>();         // games tested since the app opened
        readonly HashSet<Game> notAutoTested = new HashSet<Game>();  // the automatic test was skipped: the game was open
        readonly HashSet<Game> loaded = new HashSet<Game>();

        public GameSession(IUiDispatcher ui, Func<Game> current)
            : this(ui, GameDb.Load, g => Detector.IsRunning(g.Processes), current, work => Task.Run(work), Thread.Sleep) { }

        // tests: fake loader, process check, background and sleep
        public GameSession(IUiDispatcher ui, Action<Game> load, Func<Game, bool> gameOpen, Func<Game> current,
                           Action<Action> background, Action<int> sleep)
        {
            this.ui = ui;
            this.load = load;
            this.gameOpen = gameOpen;
            this.current = current;
            this.background = background;
            this.sleep = sleep;
        }

        public Game LoadFailed;        // the game whose servers couldn't be had (or that has none)
        public Game RetestAfterLoad;   // test this game once its servers are back (the card's "Try again")

        public bool IsLoaded(Game g) { return loaded.Contains(g); }
        public bool Tested(Game g) { return tested.Contains(g); }
        public void MarkTested(Game g) { tested.Add(g); }
        public bool NotAutoTested(Game g) { return notAutoTested.Contains(g); }
        public void SkippedAutoTest(Game g) { notAutoTested.Add(g); }
        public bool ClearSkippedAutoTest(Game g) { return notAutoTested.Remove(g); } // true = it was skipped

        // The card's "Try again" (CardKind.Offline): forget the test and the servers ("Loading" until they're back, so no
        // test runs on the old list), and test once they are.
        public void Retry(Game g)
        {
            tested.Remove(g);
            loaded.Remove(g);
            RetestAfterLoad = g;
        }

        public event Action<Game, string> Failed;   // the game on screen, the error
        public event Action<Game, bool> Loaded;     // the game on screen, and whether it is open (no automatic test then)

        // autoTest: Prefs.AutoTest, asked once the servers are in (the open-game check is only needed with it on)
        public void Load(Game g, Func<bool> autoTest)
        {
            background(() =>
            {
                string err = null;
                // one quiet retry: a slow answer from Steam now and then shouldn't show up as an error
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { load(g); err = null; break; }
                    catch (Exception ex) { err = ex.Message; sleep(1500); }
                }
                // the game is open (maybe in a match): no automatic test (performance audit item 4); the card says the pings
                // are from last time and how to refresh them (CardInput.OldPings)
                bool open = false;
                try { open = err == null && autoTest() && gameOpen(g); } catch { }
                ui.Post(() =>
                {
                    if (current() != g) return;
                    if (err != null)
                    {
                        LoadFailed = g;
                        var f = Failed;
                        if (f != null) f(g, err);
                        return;
                    }
                    loaded.Add(g);
                    if (g.Regions.Count == 0) LoadFailed = g;
                    var h = Loaded;
                    if (h != null) h(g, open);
                });
            });
        }
    }
}
