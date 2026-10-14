using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PingBoost
{
    // How the ping test reaches the network (fakeable in tests).
    interface IPingProbe
    {
        PingResult Measure(Endpoint ep, int count, int timeoutMs);
        double RelayPing(Relay relay);                    // the player's ping to their relay; < 0 = no answer
        double FromRelay(Relay relay, Endpoint target);   // the relay's own ping to the target; < 0 = no answer
    }

    sealed class LiveProbe : IPingProbe
    {
        public PingResult Measure(Endpoint ep, int count, int timeoutMs) { return Pinger.Measure(ep, count, timeoutMs); }
        public double RelayPing(Relay relay) { return relay.MeasurePing(); }
        public double FromRelay(Relay relay, Endpoint target) { return relay.ProbeFromRelay(target); }
    }

    // The ping test of one game: the player's relays first, then every region (Parallel at a time). The window keeps
    // the queue rules (what runs next); this runs the test and reports on the UI thread.
    sealed class PingTestService
    {
        // regions (and each one's addresses) at a time: fewer probes at once for about the same test time (performance audit item 4)
        public const int Parallel = 16;

        readonly IUiDispatcher ui;
        readonly IPingProbe probe;
        readonly Action<Action> background;
        readonly Action<Exception> crashed;

        public PingTestService(IUiDispatcher ui, IPingProbe probe, Action<Exception> crashed)
            : this(ui, probe, crashed, work => Task.Run(work)) { }

        // crashed: called on the test's own thread when it stops with an error (before Failed)
        public PingTestService(IUiDispatcher ui, IPingProbe probe, Action<Exception> crashed, Action<Action> background)
        {
            this.ui = ui;
            this.probe = probe;
            this.crashed = crashed;
            this.background = background;
        }

        public Game Testing { get; private set; } // the game being tested; null = none
        public Game Next;                         // asked for while another game's test ran: it goes next

        public event Action RelaysMeasured;                       // the relays' own pings are in
        public event Action<Game, Region, int, int> RegionDone;   // region, done, total
        public event Action<Game, List<Region>, long> Completed;  // the regions tested, the test's time in ms (Testing is null by then)
        public event Action<Game, Exception> Failed;              // (Testing is null by then)

        // g's test is about to start: Testing says so from now on (the card shows it before the test runs)
        public void Prepare(Game g) { Testing = g; }

        // Runs the test of the prepared game. regions: the ones to test (the window leaves out those the lock blocks);
        // relays: a copy of the player's relays
        public void Run(List<Region> regions, List<Relay> relays)
        {
            var g = Testing;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            background(() =>
            {
                try
                {
                    foreach (var rel in relays) rel.Ping = probe.RelayPing(rel);
                    ui.Post(() => { var h = RelaysMeasured; if (h != null) h(); });

                    int done = 0;
                    System.Threading.Tasks.Parallel.ForEach(regions, new ParallelOptions { MaxDegreeOfParallelism = Parallel }, r =>
                    {
                        Measure(r, relays, probe);
                        int n = Interlocked.Increment(ref done);
                        ui.Post(() => { var h = RegionDone; if (h != null) h(g, r, n, regions.Count); });
                    });

                    long ms = watch.ElapsedMilliseconds;
                    ui.Post(() =>
                    {
                        Testing = null;
                        var h = Completed;
                        if (h != null) h(g, regions, ms);
                    });
                }
                catch (Exception ex)
                {
                    crashed(ex);
                    ui.Post(() =>
                    {
                        Testing = null;
                        var h = Failed;
                        if (h != null) h(g, ex);
                    });
                }
            });
        }

        // One region: its best address (the lowest ping among those that answer), and its ping through each relay.
        public static void Measure(Region r, List<Relay> relays, IPingProbe probe)
        {
            var results = r.Endpoints.AsParallel().AsOrdered().Select(ep => probe.Measure(ep, 3, 1000)).ToList();
            PingResult best = null;
            Endpoint bestEp = null;
            for (int i = 0; i < results.Count; i++)
            {
                var res = results[i];
                if (best == null || (res.Ok && (!best.Ok || res.Avg < best.Avg))) { best = res; bestEp = r.Endpoints[i]; }
            }
            r.Direct = best ?? new PingResult();
            r.BestEndpoint = best != null && best.Ok ? bestEp : null;

            var via = new Dictionary<string, double>();
            var target = r.Target;
            foreach (var rel in relays)
            {
                if (rel.Ping < 0 || !rel.HasAgent || target == null) continue;
                double v = probe.FromRelay(rel, target);
                if (v >= 0) via[rel.Name] = rel.Ping + v;
            }
            r.ViaRelay = via;
        }
    }
}
