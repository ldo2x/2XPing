using System;
using System.Threading.Tasks;

namespace PingBoost
{
    // The update check: asks for the latest release in the background and says what the window should do about it.
    // Updater stays the low-level part (GitHub, the grace period, installing).
    sealed class UpdateService
    {
        readonly IUiDispatcher ui;
        readonly Func<Release> latest;
        readonly Func<Release, bool> forced;
        readonly Func<bool> gameRunning;
        readonly Action<Action> background;
        bool prompted;

        public UpdateService(IUiDispatcher ui, Func<bool> gameRunning)
            : this(ui, Updater.Latest, Updater.Forced, gameRunning, work => Task.Run(work)) { }

        // tests: fake release source, grace period and background
        public UpdateService(IUiDispatcher ui, Func<Release> latest, Func<Release, bool> forced, Func<bool> gameRunning, Action<Action> background)
        {
            this.ui = ui;
            this.latest = latest;
            this.forced = forced;
            this.gameRunning = gameRunning;
            this.background = background;
        }

        public Release Pending { get; set; }       // the newer release, null = up to date (the developer tools fake one)
        public bool ClosingForUpdate { get; set; } // the window closes to install: no more prompts

        public event Action<Release> Changed;      // every answer (null = up to date): the update bar
        public event Action<Release> Available;    // a newer release, while not closing for an update
        public event Action<Release> MustUpdate;   // the grace period is over: update or close
        public event Action<Release> FirstPrompt;  // the one chat line about a new version (once per session)

        public void Check(bool atStart)
        {
            background(() =>
            {
                var r = latest();
                ui.Post(() => Apply(r, atStart));
            });
        }

        void Apply(Release r, bool atStart)
        {
            Pending = r;
            Raise(Changed, r);
            if (r == null || ClosingForUpdate) return;
            Raise(Available, r);
            if (forced(r))
            {
                // don't pull the rug in the middle of a match: force it on the next launch instead
                if (!atStart && gameRunning()) return;
                Raise(MustUpdate, r);
            }
            else if (!prompted)
            {
                prompted = true;
                Raise(FirstPrompt, r);
            }
        }

        static void Raise(Action<Release> e, Release r) { if (e != null) e(r); }
    }
}
