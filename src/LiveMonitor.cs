using System;
using System.Threading;

namespace PingBoost
{
    // The live ping of the locked or boosted server: one ping a second on its own thread (one every 15 s while the
    // game is in front, just for the taskbar title). Starting again or stopping ends the previous one: each run has a
    // generation, and a sample of an older generation is never reported.
    sealed class LiveMonitor
    {
        readonly IUiDispatcher ui;
        readonly Func<bool> appFront;
        readonly Func<Endpoint, int, double> once;
        readonly Action<int> sleep;
        readonly Func<DateTime> now;
        int gen; // read by the monitor thread: always through Volatile/Interlocked

        public LiveMonitor(IUiDispatcher ui, Func<bool> appFront)
            : this(ui, appFront, Pinger.Once, Thread.Sleep, () => DateTime.Now) { }

        // tests: a fake ping, sleep and clock
        public LiveMonitor(IUiDispatcher ui, Func<bool> appFront, Func<Endpoint, int, double> once, Action<int> sleep, Func<DateTime> now)
        {
            this.ui = ui;
            this.appFront = appFront;
            this.once = once;
            this.sleep = sleep;
            this.now = now;
        }

        // One ping: the label it was started with and the ms (< 0 = no reply). On the UI thread, never after Stop.
        public event Action<string, double> Sample;

        bool Current(int g) { return g == Volatile.Read(ref gen); }

        // Follows target from now on (the previous one stops).
        public void Start(Endpoint target, string label)
        {
            int g = Interlocked.Increment(ref gen);
            var t = new Thread(() =>
            {
                var last = DateTime.MinValue;
                while (Current(g))
                {
                    // while the game is in front: one ping every 15 s, just for the taskbar title
                    if (!appFront() && now() - last < TimeSpan.FromSeconds(15)) { sleep(500); continue; }
                    last = now();
                    double v = once(target, 1500);
                    if (!Current(g)) break;
                    ui.Post(() =>
                    {
                        if (!Current(g)) return;
                        var h = Sample;
                        if (h != null) h(label, v);
                    });
                    sleep(1000);
                }
            }) { IsBackground = true };
            t.Start();
        }

        public void Stop() { Interlocked.Increment(ref gen); }
    }
}
