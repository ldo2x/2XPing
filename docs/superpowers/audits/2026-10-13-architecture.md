# 2X Ping: architecture audit and target design

Repo: `C:\Dev\2XPing`, branch `simple-main-screen` (HEAD 3eee989). Read-only audit; nothing was changed.
Scope read: every `src/*.cs` (MainForm.cs in full, 3123 lines), `tests/*.cs` + `tests/run-tests.ps1`, `build.ps1`,
`docs/superpowers/specs/2026-10-13-simple-main-screen-design.md`.

Constraints the design respects: C# 5 (no `$""`, `?.`, `=>` members, `nameof`), .NET Framework 4.8, WinForms,
Windows `csc.exe` (no NuGet, no project file), the custom test runner (`static partial class Tests`, `Check`, `Run`).

---

## 0. Summary

- `MainForm` is a 3,000-line class doing about 15 jobs. It holds roughly 70 fields, and it reads the selected game straight from a
  control (`CurrentGame => gameList.SelectedItem`, line 77), so no logic can run or be tested without the UI.
- Most of the logic is already pure and easy to pull out: card input, choice building, lock planning, match
  classification, route advice, relay routing, loss tracking and held notices. The risky parts are the
  stateful system-touching services: firewall, pktmon, WireGuard and the live-ping thread.
- Target: one long-lived `MainForm` shell, a `MainController` that holds the app's services, small services that keep their
  state on the UI thread and raise their events through a single `IUiDispatcher`, and passive views that are rebuilt on Settings changes.
- The migration has 20 steps. Each step builds and passes `tests/run-tests.ps1`. The pure extractions go first and the lock/firewall work goes last.
- Six real bugs turned up along the way (section 5.1). The worst three are: settings changes get lost while a detect or reset is running,
  pressing Stop while the relay route is being built leaves the WireGuard tunnel up, and the firewall apply/remove calls race each other.

---

## 1. Responsibility map of MainForm today

### 1.1 Sections (src/MainForm.cs)

| Lines | Section | What it does | State it owns |
|---|---|---|---|
| 14-78 | Fields, theme | Theme colours (17-28, also used by every other file as `MainForm.Accent` etc.), server-list column consts (33-36), `GroupHeader` (39-44), core state, `CurrentGame` (77), `LockedOut` (78) | `allGames, games, relays, simple, busy, boosting, clickedCheck, tested, tunnelActive (volatile), monitorGen, lockSummary, lockInfo, activity` + all control fields 55-67 |
| 80-163 | Constructor | Language/prefs/dev mode load, `Issues.Reported` hook (89-93), layout, startup cleanup Task (112-119: leftover lock, own pktmon filter, firewall off), WireGuard note, `Shown` (welcome/consent, telemetry start, slow-startup issue, `SelectStartGame`, `StartAutoDetect`, beta notices, update check + 3 h timer), `FormClosing` teardown (155-162) | update timer (unreferenced local) |
| 165-174 | SelectStartGame | Running game, else last game, else first | - |
| 176-321 | Window/taskbar | Minimize tracking, `Activated` comeback refresh (202-208), foreground P/Invoke `appActive` (218-228), **player-stats refresh scheduling misplaced here** (229-238), `BringUp`/`RestoreWindow` (241-255), taskbar title (260-278), `FlashTaskbar` (285-291), held notices (293-314), `ApplyDirection` | `wasMinimized, restoreState, liveStatus, heldNotices, lastInFront, statsAt` |
| 323-460 | BuildUi | Header (live chip, Settings, Feedback, logo 5-click dev toggle 347-354, BETA/DEV badges), status bar, game list, tab bar + pages, update bar, dev strip, Arabic mirroring (458) | `currentPage` |
| 462-491 | HeaderButton, MirrorChildren | RTL mirroring of nested docks (skips ListView/ListBox/ComboBox/TextBox/StateCard) | - |
| 493-518 | BuildStateCard | Card host + event wiring (button, selection, other regions, manual, test link) | `cardHost, stateCard` |
| 520-623 | BuildServersPage | Lock bar, server list + tick-box hit testing, test/route/BOOST/Stop bar, graph, list card, info bar, card | `pickBar, btnLock, btnUnlock, lockLabel, serverList, bottomPanel, btnTest, routeBox, btnBoost, btnStop, graph, listCard, ...` |
| 625-686 | Relays page, Tools page, ToolButton, MakeButton | Tools page keeps the old log text across rebuilds (660) | `relayList, logBox` |
| 688-790 | ListView styling | Owner-drawn rows, hover row (static `hotRow`, 689), RTL column order, `FirstCell`, `CheckBox` | static `hotRow` |
| 792-836 | DrawRegionCheck | Tick box + flag + name cell | - |
| 838-907 | ShowPage, FillGames, DrawGameItem | Tabs, game list filtering (hidden games), owner-drawn game list with TRIAL / IN DEVELOPMENT tags | `hotGame` |
| 909-964 | OpenSettings, FitAdvancedHeight, RebuildUi | Settings dialog -> rebuild or `pendingRebuild` while `busy` | `pendingRebuild` |
| 966-1052 | Helpers | `Ui()` marshalling (968), `Log`, `SetStatus`, notices `Msg`/`AdvancedMsg` (985-1011), `Ms`, `ShortName`, `PingColor`, `SetSub`, relay routing `BestRelay`/`AutoRelay`/`BestLatency` | `chat, centerPanel` |
| 1054-1122 | OnGameChanged | Resets the card/match, loads the server list on a Task (one retry), last results, list fill, stats, auto test | - |
| 1124-1204 | Deadlock grouping | `Grouped`, `RegionKey`, `PlayersOf`, `FillServers`, `AddRegionRow`, group header text/drawing (Deadlock AppId "1422450" hard-coded) | `deadlockOnline` |
| 1206-1445 | State card logic | Choice building/keeping the user's selection, `UpdateCard` (builds `CardInput`), card layout/scroll (1338-1388), card button actions, lock names, `LockRegions` | `allChoices, featured, everyChoice, choicesGame, choicesSig, userPicked, expanded, manualOpen, testGame, testNext, retestGame, loadFailed, lockGame, lockingGame, loadedGames, lockNames, matchRegion, currentServer, matchOnPicked, matchViaRelay, currentMs` |
| 1447-1502 | UpdateRow/UpdateRegion | One server-list row (ping/jitter/loss/players/wait/relay/route) | - |
| 1504-1608 | RunTest | Relay pings, Parallel.ForEach(64) per region and endpoint, relay probes, queueing (`testNext`), completion (save, rebuild, chain next test, telemetry, issues) | uses `busy, testGame, testNext, pendingRebuild` |
| 1610-1687 | Updates | Check, bar text, forced update, "updated" note (10 s timer) | `updateBar, updateLabel, updateBtn, pendingUpdate, updatePrompted, closingForUpdate` |
| 1689-1752 | Info bar + LoadStats | Detect button, player-numbers line, Deadlock activity/online/Steam fetch on a Task, regroup list | - |
| 1754-1812 | Detect helpers | `ValveRegionOf` (wrapper), `ServerHits` (tested), Overwatch zones memory (`SeenZones` cache, `LearnZones`), `HostRegionOf`, `RelayRegionOf` | `seenZonesCache` |
| 1814-1977 | Manual detect | 15 s pktmon capture, traffic.txt, telemetry, `ShowValveResult` (UI thread), `ShowOtherResult` (runs on the background thread, then `Ui`) | sets `busy` |
| 1979-2180 | Automatic detect | 10 s WinForms timer, `Verifying` window, `LockChanged`, `AutoDetectTick` (8 s capture on a Task), `CheckLockRules` (2 min), `ShowAutoDetect` (match state, zones, no-match timer, issues, notices, telemetry) | `autoDetectTimer, autoDetecting, matchSinceLock, dcSinceLock, noMatchLong, observedSinceLock, lastObservedCheck, lastAutoDetect, lockChangedAt, lastRulesCheck, lastAutoServer` |
| 2182-2385 | Server lock | `TogglePick`, `UpdateLockBar`, `LockServers` (exe lookup, block-list planning, `ServerLock.Apply` on a Task, ~10 notices), `WarnLongerRoute` (Valve backbone maths), `UnlockServers` | uses lock fields |
| 2387-2440 | ResetEverything | Clears lock/picks/boost, removes rules/tunnel/pktmon, resets priorities, verifies | sets `busy` |
| 2442-2612 | BOOST + live ping | `Boost` (route choice, tunnel on a Task), `StartMonitor` (dedicated thread, 1 s, 15 s while in game), `TrackLoss`, `StopMonitor`, `StartLockMonitor`, `StopAll`, `RaisePriority` | `monitorGen, lossWindow, lossLabel, boosting, tunnelActive` |
| 2614-2697 | Relays | List/route box refresh, add (.conf + InputDialog), remove, test | - |
| 2699-2709 | Tools | `FlushDns` (blocks the UI thread up to 10 s) | - |
| 2711-3007 | Developer mode | Toggle (dev.key), strip, page, lock test (60 s pktmon on a Task), `DevOut`, self-test, firewall rules dump, IP-list refresh, ranges, test notices, fake update bar, open files, reset first run | `devMode, devOut, lockTesting, devLockTestBtn` |
| 3011-3049 | InputDialog | Generic dialog (belongs in its own file) | - |
| 3051-3122 | PingGraph | Graph control (belongs in its own file) | - |

### 1.2 Call graph (main flows)

```
Shown -> SelectStartGame -> gameList.SelectedItem = g -> OnGameChanged
OnGameChanged -> [Task] GameDb.Load -> Ui: FillServers -> RebuildChoices, UpdateCard
                                       -> UpdateLockBar, LoadStats -> [Task] Stats.* -> Ui: FillServers
                                       -> RunTest (auto test / retest)
RunTest -> [Task] Relay.MeasurePing, Parallel(Pinger.Measure, Relay.ProbeFromRelay)
        -> Ui per region: UpdateRegion, SetStatus
        -> Ui end: tested.Add, Storage.SaveLast, (pendingRebuild -> RebuildUi) | chain RunTest | RebuildChoices, UpdateCard, FillServers, Telemetry, Issues
StateCard.ButtonPressed -> CardButton -> LockRegions -> FillServers, UpdateLockBar, LockServers
                                      -> UnlockServers | RunTest | OnGameChanged (retry)
LockServers -> WarnLongerRoute, CardLockNames -> [Task] IpLists.Resolve/Subtract, ServerLock.Apply
            -> Ui: LockChanged -> UpdateCard; UpdateLockBar, FillServers, StartLockMonitor -> StartMonitor
autoDetectTimer (10 s) -> AutoDetectTick -> CheckLockRules -> [Task] ServerLock.Exists -> Ui: Msg
                       -> [Task] Detector.Capture(8 s), Stats.Locate, Pinger.Measure -> Ui: ShowAutoDetect
                                 -> LearnZones, SetMatch, UpdateCard, FillServers, Issues, AdvancedMsg, Telemetry
                       -> RefreshStats(5 min) -> LoadStats
btnDetect -> DetectServer -> [Task] Detector.Capture(15 s) -> ShowValveResult (Ui) | ShowOtherResult (bg then Ui)
Boost -> RaisePriority, StartMonitor | [Task] Tunnel.Start -> Ui: StartMonitor
StartMonitor -> [Thread] Pinger.Once every 1 s -> Ui: liveLabel, SetLiveStatus, graph, TrackLoss
OpenSettings / SetDevMode -> RebuildUi (or pendingRebuild if busy) -> BuildUi, re-select game -> OnGameChanged
FormClosing -> monitorGen++, Tunnel.Stop, ServerLock.Remove, Detector.Cleanup (lock test), Telemetry.Exit
Program listener thread -> form.BeginInvoke(BringUp)
```

### 1.3 Threads and timers

| Kind | Where | Period / length | Touches |
|---|---|---|---|
| WinForms Timer `autoDetectTimer` | 1999 | 10 s | AutoDetectTick, RefreshStats(5 min) |
| WinForms Timer (local, never kept) | 151 | 3 h | CheckForUpdate |
| WinForms Timer one-shot | 1670 | 10 s | update bar note |
| WinForms Timers in controls | StateCard dots, RoundButton/SoftControl/Toggle anims, ToastForm, DevPanel 30 s | - | UI only |
| System.Threading.Timer | Telemetry heartbeat | 60 s | network |
| Dedicated Thread | StartMonitor (2519) | 1 s (15 s while the game is in front) | Pinger.Once; reads `monitorGen` (not volatile) and `appActive` (P/Invoke, writes `lastInFront`) |
| Dedicated Thread | Program show-signal listener | event | `form.BeginInvoke(BringUp)` |
| Task.Run | 112, 1077, 1525 (+Parallel.ForEach 64), 1620, 1713, 1836 (15 s capture), 2046 (8 s capture), 2093, 2248, 2379, 2410, 2484, 2590, 2686, 2870 (60 s capture), 2911, 2939 | - | network, netsh, pktmon, WireGuard, Storage |
| ThreadPool | Telemetry.Send/Start, DevPanel.Reload, UpdateDialog install | - | network, Storage |

Mutual-exclusion flags, all read and written on the UI thread: `busy` (ping test, **manual detect** and **reset**: one flag for
three different jobs), `autoDetecting`, `lockTesting`, `boosting`, `lockingGame`. Of these, only pktmon really needs exclusion (one
capture session at a time). The firewall and the tunnel also need it but have none.

---

## 2. Target design

### 2.1 Principles

1. **Single-threaded state.** Every service keeps its state on the UI thread, the same model the code uses today. Background work
   gets *snapshots* as input and hands *results* back through `IUiDispatcher.Post`. No locks are needed inside services.
2. **Services raise events on the UI thread**, so subscribers never marshal. `Ui()` moves into one `ControlDispatcher`.
   Tests use `InlineDispatcher` (it runs the action straight away), so the services can be tested without a form.
3. **One subscriber per service: the `MainController`.** Views are passive. They get `Render(...)` calls and raise intent
   events (`LockPressed`, `TilesChanged`, ...). On `RebuildUi` the controller gets new views and services keep running, so nothing is
   left subscribed to disposed controls.
4. **Pure logic in static classes** (`CardInputBuilder`, `LockPlanner`, `RouteAdvisor`, `MatchClassifier`, `Routing`,
   `LossTracker`, `HeldNotices`, `ChoiceSet`). This is where the tests go.
5. **System side effects are serialized:** one `FirewallWorker` queue for netsh + WireGuard, and one `CaptureGate` for pktmon.
6. `MainForm` stays the long-lived window. `Program`'s `BringUp` and `RebuildUi` both rely on that one instance.

### 2.2 File layout

`build.ps1` compiles `src\*.cs` and `tests/run-tests.ps1` takes `src\*.cs`. **Neither looks in subfolders.** Either keep `src/` flat
(the choice this plan makes, with name prefixes) or change both scripts in the same commit (`/recurse:src\*.cs` for csc, and
`Get-ChildItem -Recurse` that still leaves out `Program.cs`).

```
src/
  Program.cs                 (unchanged)
  Theme.cs                   colours from MainForm 17-28 + DevColor/DevAccent/RowHover
  Lang.cs                    from GameDb.cs 11-16
  Storage.cs                 from GameDb.cs 485-683 (+ SettingsStore cache)
  Fmt.cs                     Ms, ShortName, PingColor (one copy)
  UiDispatcher.cs            IUiDispatcher, ControlDispatcher, InlineDispatcher, Background
  Game.cs / GameDb.cs        Region, Game, GameDb (data only)
  Zones.cs                   from RegionGroups.cs 23-152
  Routing.cs                 BestRelay, AutoRelay, BestLatency, Best, SortForList
  GameSession.cs             current game, loaded/failed/tested, server loading
  PingTestService.cs         ping test + queue
  StatsService.cs            PlayerStats + fetching
  LockService.cs             LockState, LockService, LockPlanner, RouteAdvisor
  FirewallWorker.cs          serial queue for ServerLock + Tunnel
  ServerLock.cs              from Tunnel.cs 11-78
  Detector.cs                from Stats.cs 182-333
  DetectService.cs           MatchClassifier, MatchState, CaptureGate, DetectService, ZoneMemory
  LiveMonitor.cs             LiveMonitor, LossTracker, BoostService
  RelayStore.cs              relays list + tests
  UpdateService.cs           update checks (Updater stays the low-level part)
  Scheduler.cs               one 10 s tick + 3 h update tick + foreground tracking
  NoticeCenter.cs            HeldNotices, NoticeCenter, AppLog
  ChoiceSet.cs               tiles: all/featured/every + keep the user's selection
  CardInputBuilder.cs        pure: snapshots -> CardInput
  MainController.cs          owns the services, wires events to views
  MainForm.cs                shell: window, title, taskbar, BringUp, RebuildUi, teardown (~400 lines)
  View*.cs                   ViewHeader, ViewGameList, ViewServerList, ViewCardHost, ViewInfoBar, ViewLockBar,
                             ViewBoostBar, ViewRelays, ViewTools, ViewUpdateBar, ViewDev (strip + page)
  ListStyle.cs               MakeListView, RowBack, FirstCell, Align, hover row (per list, not static)
  PingGraph.cs, InputDialog.cs, DevPanel.cs, WelcomeDialog.cs   (moved out of MainForm.cs / Telemetry.cs)
  (Brand, Fonts, CardLogic, StateCard, RegionGroups, IpLists, LockTest, SystemInfo, Telemetry, Toast, Ui, Update, Issues, Net: stay)
```

### 2.3 Public interfaces (C# 5)

#### Infrastructure

```csharp
interface IUiDispatcher
{
    void Post(Action a);          // runs a on the UI thread (now if already on it); dropped once the form is disposed
    bool OnUiThread { get; }
}

sealed class ControlDispatcher : IUiDispatcher            // the body of today's MainForm.Ui (968-973)
{
    public ControlDispatcher(Control owner);
}

sealed class InlineDispatcher : IUiDispatcher { }         // tests: runs at once

static class Background
{
    // Task.Run with exceptions turned into Issues.Report(code, ...) instead of unobserved task faults
    public static void Run(string issueCode, Action work);
    // TaskCreationOptions.LongRunning: for the 8-60 s pktmon captures that sleep (they shouldn't hold the 64 min pool threads)
    public static void RunLong(string issueCode, Action work);
}

static class SettingsStore    // behind Storage.GetSetting/SetSetting
{
    public static string Get(string key);                 // in-memory dictionary, loaded once
    public static void Set(string key, string value);     // lock + write-through
    internal static void UseDir(string dir);              // tests: a temp folder, never the player's settings.txt
}
```

#### Session and ping test

```csharp
sealed class GameSession
{
    public GameSession(List<Game> all, IUiDispatcher ui, Func<Game, string> load);   // load: GameDb.Load wrapped, returns error or null
    public List<Game> All { get; }
    public List<Game> Available { get; }                  // GameDb.Available(All) under DevMode
    public bool DevMode { get; set; }                     // also sets GameDb.ShowUnreleased (to remove that global later)
    public Game Current { get; }
    public void Select(Game g);                           // replaces reading gameList.SelectedItem
    public bool IsLoaded(Game g);
    public bool LoadFailed(Game g);
    public bool Tested(Game g);
    public void MarkTested(Game g);
    public void Retry(Game g);                            // CardKind.Offline: forget loaded + tested, reload, test after
    public Game RetestAfterLoad { get; }
    public event Action<Game> CurrentChanged;
    public event Action<Game> ServersLoaded;
    public event Action<Game, string> ServersFailed;
}

interface IPingProbe
{
    PingResult Measure(Endpoint ep, int count, int timeoutMs);
    double Once(Endpoint ep, int timeoutMs);
}

sealed class PingTestResult
{
    public Game Game;
    public List<Region> Regions;
    public Region Best;                                   // null = nothing answered
    public long Ms;
    public List<string> Down;
    public Dictionary<string, object> Data;               // telemetry payload, built here instead of in the UI
}

sealed class PingTestService
{
    public PingTestService(IUiDispatcher ui, IPingProbe probe, Func<List<Relay>> relays, Func<Game, Region, bool> lockedOut);
    public bool Running { get; }
    public Game Testing { get; }
    public Game Next { get; }
    public bool TestingOrQueued(Game g, bool tested, bool autoTest);   // the rule in UpdateCard 1303
    public void Request(Game g);                          // run now, or queue behind another game's test
    public event Action RelaysMeasured;
    public event Action<Game, Region, int, int> RegionDone;   // region, done, total
    public event Action<PingTestResult> Completed;
}

static class Routing
{
    public const double RelayMarginMs = 3;
    public static KeyValuePair<string, double>? BestRelay(Region r);
    public static Relay AutoRelay(Region r, IList<Relay> relays);
    public static double BestLatency(Region r);
    public static Region Best(IEnumerable<Region> regions);          // replaces 4 copies (1288, 1451, 1580, 2575)
    public static List<Region> SortForList(Game g, Func<Region, bool> lockedOut);  // 1106, 1162, 1596
}
```

#### Stats

```csharp
sealed class PlayerStats                                  // immutable snapshot
{
    public Dictionary<string, int> Activity;              // Deadlock matches/hour per matchmaking region, null = none
    public int Online = -1, Steam = -1;
    public int PlayersOf(int matches);                    // MainForm 1138
    public string RegionKey(Region r);                    // MainForm 1127
    public bool Grouped(Game g);                          // MainForm 1125 (no "1422450" string at the call site)
}

sealed class StatsService
{
    public StatsService(IUiDispatcher ui, Func<Game, PlayerStats> fetch);   // fetch = Stats.* calls, fakeable in tests
    public PlayerStats For(Game g);                       // last known, never null
    public void Refresh(Game g, bool quiet);
    public void RefreshIfStale(Game g, TimeSpan fresh);   // RefreshStats 233
    public event Action<Game, PlayerStats, bool> Updated; // quiet flag: the view keeps its scroll position
}
```

#### Lock

```csharp
sealed class LockState                                    // immutable; null = unlocked
{
    public Game Game;
    public string Summary;                                // lockSummary
    public string CardNames;                              // lockNames
    public LockTest.LockInfo Info;
    public int Ranges;
    public bool FirewallOn;
    public DateTime At;
}

static class LockPlanner                                  // pure: LockServers 2252-2272
{
    public static LockTest.LockInfo Plan(Game g, List<Region> picked, string exe, Func<IEnumerable<string>, List<string>> resolve);
    public static string Scope(LockTest.LockInfo info, int ranges);   // MainForm.LockScope 2845
    public static string CardNames(Game g, List<Region> picked, List<RegionChoice> choices);  // CardLockNames 1425
}

sealed class LongerRoute { public Region Through, To; public int Ms; }
static class RouteAdvisor                                 // pure: WarnLongerRoute 2328-2354
{
    public static LongerRoute Find(Game g, List<Region> picked);      // null when < 10 ms or not Valve
}

sealed class FirewallWorker : IDisposable                 // one background thread, FIFO
{
    public void Enqueue(Action op, Action<Exception> failed);
    public void Drain(TimeSpan max);                      // FormClosing: let an Apply in progress finish, then Remove
}

sealed class LockService
{
    public LockService(IUiDispatcher ui, FirewallWorker fw);
    public LockState Current { get; }
    public Game Applying { get; }                         // lockingGame
    public bool LockedOut(Game g, Region r);              // MainForm 78
    public void Lock(Game g, List<Region> picked, string exe, string cardNames);
    public void Unlock();
    public void ResetAll(IEnumerable<Game> games);        // picks, rules, tunnel, pktmon, priorities
    public void CheckRulesIfDue(DateTime now);            // CheckLockRules 2088 (every 2 min)
    public event Action<LockState> Locked;
    public event Action<Game, Exception> LockFailed;
    public event Action Unlocked;
    public event Action<bool> ResetDone;                  // true = rules left behind
    public event Action<string> RulesMissing;
}
```

#### Detection

```csharp
enum MatchKind { NotRunning, NoMatch, ViaRelay, OnDatacenter, OnUnknownServer }

sealed class MatchOutcome
{
    public MatchKind Kind;
    public Region Datacenter, Relay;
    public List<Region> Found;                            // manual check names every hosting datacenter
    public string Where;                                  // city (games without datacenter lists)
    public int WhereMs = -1;
    public bool OnPicked;
    public List<Detector.Hit> Hits;
}

static class MatchClassifier                              // pure: shared by ShowValveResult and ShowAutoDetect
{
    public static List<Detector.Hit> ServerHits(Game g, List<Detector.Hit> hits);   // moved from MainForm 1761 (tests use it)
    public static Region HostRegionOf(Game g, List<Detector.Hit> hits);
    public static Region RelayRegionOf(Game g, List<Detector.Hit> hits);
    public static MatchOutcome Classify(Game g, List<Detector.Hit> hits, bool locked);
    public static string RegionNameOf(Game g, Region dc);  // MainForm 1242
}

sealed class MatchState
{
    public string Region; public bool OnPicked, ViaRelay;
    public string CurrentServer; public int CurrentMs = -1;
    public bool Set(string region, bool onPicked, bool viaRelay);   // true = a different match (drop held warnings)
    public void SetServer(string server, int ms);
    public bool Clear();                                   // true = something was cleared
    public bool Any { get; }
}

sealed class CaptureGate                                  // pktmon is global: one capture at a time
{
    public bool TryEnter(string who);                     // "auto", "manual", "locktest"
    public void Exit();
    public string Holder { get; }
}

sealed class ZoneMemory                                   // MainForm 1767-1800
{
    public List<string> Seen(Game g);
    public void Learn(Game g, IEnumerable<Region> datacenters);
}

sealed class DetectService
{
    public DetectService(IUiDispatcher ui, CaptureGate gate, ZoneMemory zones,
                         Func<string[], int, List<Detector.Hit>> capture, Func<string, string> locate, IPingProbe probe);
    public MatchState Match { get; }
    public bool NoMatchLong { get; }
    public bool Verifying(DateTime now);
    public void LockChanged(DateTime now);                // resets dcSinceLock/observed/...
    public void Tick(Game g, bool appFront, LockState lockState);      // automatic, every 30 s / 90 s
    public void CheckNow(Game g, LockState lockState);    // the 15 s manual check
    public event Action<Game, MatchOutcome> AutoResult;
    public event Action<Game, MatchOutcome> ManualResult;
    public event Action<Game, string> Failed;
    public event Action MatchChanged;
}
```

#### Live ping and BOOST

```csharp
sealed class LossTracker                                  // pure: TrackLoss 2549
{
    public bool Add(string label, double ms, out int lost);           // true = 15%+ of the last 60 lost
}

sealed class LiveMonitor
{
    public LiveMonitor(IUiDispatcher ui, Func<bool> appFront, Func<Endpoint, int, double> once);
    public string Label { get; }
    public void Start(Endpoint target, string label);    // stops the previous one (generation stays private, Interlocked)
    public void Stop();
    public event Action<string, double> Sample;           // UI thread
}

sealed class BoostService
{
    public BoostService(IUiDispatcher ui, FirewallWorker fw, LiveMonitor monitor);
    public bool Boosting { get; }
    public bool Building { get; }                         // the tunnel is being built: Stop cancels it (fixes 5.1 #2)
    public void Start(Game g, Region region, Relay relay);// relay null = direct
    public void Stop();
    public event Action<string> Started;                  // label
    public event Action<string> Failed;
    public event Action Stopped;
}
```

#### App-level

```csharp
sealed class Scheduler : IDisposable                      // replaces autoDetectTimer, the 3 h timer, appActive, Activated logic
{
    public Scheduler(Form owner);
    public bool AppFront { get; }                         // P/Invoke foreground check (MainForm 218)
    public event Action Tick;                             // 10 s
    public event Action UpdateTick;                       // 3 h
    public event Action CameBack;                         // Activated after a 20 s absence
}

sealed class HeldNotices                                  // pure: MainForm 293-314
{
    public const int Max = 4;
    public void Hold(string text, ToastKind kind, bool aboutMatch);
    public void DropMatchWarnings();
    public List<KeyValuePair<string, ToastKind>> TakeAll();
}

sealed class NoticeCenter
{
    public NoticeCenter(Func<bool> minimized, Func<bool> simple, Action<string, ToastKind> show, Action flash);
    public void Say(string text, ToastKind kind);         // Msg
    public void SayAdvanced(string text, ToastKind kind); // AdvancedMsg (aboutMatch = true)
    public void MatchChanged();                           // DropHeldWarnings
    public void FlushHeld();
}

sealed class AppLog                                       // keeps the log text, so RebuildUi no longer copies it out of logBox
{
    public string Text { get; }
    public void Add(string line);                         // thread-safe, posts to the UI
    public void Status(string text);
    public event Action<string> Line;
    public event Action<string> StatusChanged;
}

sealed class UpdateService
{
    public UpdateService(IUiDispatcher ui, Func<Release> latest);
    public Release Pending { get; }
    public bool ClosingForUpdate { get; set; }
    public void Check(bool atStart, Func<bool> gameRunning);
    public event Action<Release> Changed;                 // bar
    public event Action<Release> FirstPrompt;             // the one chat line
    public event Action<Release> MustUpdate;              // forced
}

sealed class ChoiceSet                                    // MainForm 1208-1283
{
    public List<RegionChoice> All { get; }
    public List<RegionChoice> Featured { get; }
    public List<RegionChoice> Every { get; }
    public bool UserPicked, Expanded, ManualOpen;
    public void Reset();
    public bool Rebuild(Game g, List<RegionChoice> fresh);   // keeps the player's selection when the regions are still there
}

static class CardInputBuilder                             // pure: UpdateCard 1297-1327
{
    public static CardInput Build(Game g, GameSession s, PingTestService t, bool autoTest, LockService l,
                                  MatchState m, bool noMatchLong, ChoiceSet c, List<string> seenZones);
}

sealed class MainController
{
    public MainController(MainForm shell, IUiDispatcher ui);   // builds every service once
    public void AttachViews(Views v);                     // after BuildUi / RebuildUi
    public void Start();                                  // Shown: telemetry, start game, scheduler, update check
    public void Teardown();                               // FormClosing, in today's order (section 5.6)
}
```

### 2.4 What state moves where

| Today (MainForm) | Goes to |
|---|---|
| `allGames, games, CurrentGame, tested, loadedGames, loadFailed, retestGame` | `GameSession` |
| `busy (ping test part), testGame, testNext` | `PingTestService` |
| `activity, deadlockOnline, statsAt` | `StatsService` / `PlayerStats` |
| `lockSummary, lockGame, lockNames, lockInfo, lockingGame, lastRulesCheck` | `LockService` / `LockState` |
| `matchRegion, matchOnPicked, matchViaRelay, currentServer, currentMs` | `MatchState` |
| `autoDetecting, matchSinceLock, dcSinceLock, noMatchLong, observedSinceLock, lastObservedCheck, lastAutoDetect, lockChangedAt, lastAutoServer` | `DetectService` |
| `busy (manual detect part), lockTesting (capture part)` | `CaptureGate` |
| `seenZonesCache` | `ZoneMemory` |
| `monitorGen, lossWindow, lossLabel` | `LiveMonitor`, `LossTracker` |
| `boosting, tunnelActive` | `BoostService` |
| `relays` | `RelayStore` |
| `pendingUpdate, updatePrompted, closingForUpdate` | `UpdateService` |
| `heldNotices, chat` | `NoticeCenter` / `HeldNotices` |
| log text | `AppLog` |
| `allChoices, featured, everyChoice, choicesGame, choicesSig, userPicked, expanded, manualOpen` | `ChoiceSet` |
| `autoDetectTimer`, 3 h timer, `lastInFront`, `appActive` | `Scheduler` |
| `devMode, devOut, devLockTestBtn` | `ViewDev` + `GameSession.DevMode` |
| `wasMinimized, restoreState, liveStatus, simple, currentPage, pendingRebuild` | stay in `MainForm` (shell) |
| `hotGame, hotRow (static), clickedCheck` | the views (`hotRow` per ListView, not static) |
| `busy (reset part)` | `LockService.ResetAll` in progress |

### 2.5 How the UI subscribes

- Services raise every event through `ui.Post`, so `MainController` handlers always run on the UI thread and call views
  directly. The rule is: **a background lambda never touches a field of a service or a control**. It computes and then posts.
- Example (ping test):
  ```csharp
  tests.RegionDone += (g, r, done, total) => { if (g == session.Current) views.Servers.UpdateRegion(r); log.Status(...); };
  tests.Completed += res => { session.MarkTested(res.Game); if (pendingRebuild) { RebuildNow(); return; } ... UpdateCard(); };
  ```
- `UpdateCard()` becomes one controller method:
  `views.Card.Show(CardLogic.Decide(CardInputBuilder.Build(...)), choices.Expanded ? choices.Every : choices.Featured)`.
  Every service event that changes the card calls it. Card layout (`ApplyCardLayout`, `FitCard`) moves to `ViewCardHost`.
- `LiveMonitor.Sample` goes to the header chip, the graph, the taskbar title (`MainForm.SetLiveStatus`) and `Telemetry.Ping`.
- `Issues.Reported` in developer mode: the controller subscribes once, posts through the dispatcher, and calls `notices.Say` plus `ViewDev.Out`.
- Views raise intent events only (`ViewLockBar.LockPressed`, `ViewServerList.PickToggled(Region)`, `ViewCardHost.ButtonPressed`,
  `ViewBoostBar.BoostPressed`). After `RebuildUi` the controller drops the old view references and hooks the new ones. Services are
  never subscribed to views, so disposed controls can't leak or throw.

---

## 3. Migration order

Every step: run `powershell -ExecutionPolicy Bypass -File build.ps1` and then `tests/run-tests.ps1`, both green. Steps that move UI code
also need a manual smoke pass (**S**). **S** means: launch it in Arabic and English, Simple and Advanced, developer mode on and off. Pick each of the
4 released games, run a test, lock from a tile, unlock, open "Pick by hand", change Settings while a test runs, minimize and restore,
start a second copy (BringUp), and close while locked (the rules must be gone).

| # | Step | Files | Proof |
|---|---|---|---|
| 1 | **Delete dead code** (section 4.1). No behaviour change | GameDb.cs, MainForm.cs | build + all tests |
| 2 | **Theme.cs**: move the colours. Keep `MainForm.Bg = Theme.Bg` aliases for one step, then repoint CardLogic/StateCard/Toast/Ui/dialogs/tests | Theme.cs, all users | build + tests (CardTests compare `MainForm.Accent`/`Good` and must switch to `Theme.*`) |
| 3 | **File moves only** (Lang, Storage, Zones, ServerLock, Detector, DevPanel, WelcomeDialog, InputDialog, PingGraph). Keep `src/` flat or update both build globs | new files | build + tests; `git diff -M` shows only renames/moves |
| 4 | **SettingsStore** cache + lock behind `Storage.GetSetting/SetSetting`, plus `UseDir` for tests | Storage.cs | new tests: set/get round trip; 8 threads × 50 keys all kept; a file written outside the cache is picked up on load; old `PingBoost` folder migration unchanged |
| 5 | **Fmt + Routing** (pure): merge `Short`/`ShortName`, one `PingColor`, `BestRelay/AutoRelay/BestLatency/Best/SortForList` | Fmt.cs, Routing.cs, MainForm.cs, StateCard.cs | new tests: relay margin 3 ms, direct down -> relay, sort puts locked-out regions last, ShortName strips `  [x]`/`  (x)` |
| 6 | **ChoiceSet + CardInputBuilder** (pure) out of RebuildChoices/UpdateCard | ChoiceSet.cs, CardInputBuilder.cs | new tests: the player's selection survives a rebuild with the same keys; the signature stops pointless rebuilds; `Testing` rule with testNext/autoTest; ZoneWarning only for lockable games with zones |
| 7 | **LockPlanner + RouteAdvisor** (pure) | LockService.cs (static part) | new tests: Valve keeps relay-only pops open; LockSpace = space minus picked minus allow (Overwatch service hosts never blocked); Scope string; RouteAdvisor 10 ms threshold with a fake backbone |
| 8 | **MatchClassifier** (pure), one classifier for the manual and automatic checks | DetectService.cs (static part) | existing `ServerHits` tests repointed; new: Deadlock relay-only hits -> ViaRelay; hosting dc + picked -> OnPicked; Arena Breakout hits -> OnUnknownServer; Overwatch voice + Irvine -> NoMatch |
| 9 | **LossTracker, HeldNotices** (pure) | LiveMonitor.cs, NoticeCenter.cs | new tests: 8/60 lost = no report, 9/60 (15%) = report, fewer than 60 samples = never; label change resets; HeldNotices max 4, dedupe, DropMatchWarnings keeps Bad |
| 10 | **IUiDispatcher + Background** (MainForm.Ui forwards to ControlDispatcher) | UiDispatcher.cs, MainForm.cs | build + S (quick); InlineDispatcher test |
| 11 | **StatsService** (first stateful service) | StatsService.cs, MainForm.cs | test with a fake fetch: Updated raised once; stale/fresh logic; PlayersOf with and without the online total |
| 12 | **UpdateService + ViewUpdateBar** | UpdateService.cs, ViewUpdateBar.cs | tests: the first prompt only once; forced + game running + not at start -> no MustUpdate; S: DevFakeUpdateBar, "Updated to" note |
| 13 | **RelayStore + ViewRelays** | RelayStore.cs, ViewRelays.cs | test: add/remove round trip in a temp dir; S: relays page, route box keeps its selection |
| 14 | **AppLog + NoticeCenter** wired (Msg/AdvancedMsg/Log/SetStatus forward) | NoticeCenter.cs, MainForm.cs | tests: minimized + Info is dropped, minimized + Warn is held, simple + AdvancedMsg is dropped unless minimized; S: notices, minimize -> flash -> restore shows the held ones; the log survives RebuildUi |
| 15 | **Scheduler** (one 10 s tick, 3 h tick, CameBack) | Scheduler.cs, MainForm.cs | S: the auto check still pauses while the game is in front and refreshes on return (watch DevOut / traffic.txt timestamps) |
| 16 | **LiveMonitor + BoostService** (+ fix 5.1 #2, #6) | LiveMonitor.cs | tests with a fake `once`: Stop -> no Sample after; Start twice -> only the new label samples; Stop while Building -> tunnel stop queued; S: BOOST direct, relay (if available), Stop, lock monitor after Stop |
| 17 | **GameSession + PingTestService** (CurrentGame stops reading `gameList`) | GameSession.cs, PingTestService.cs | tests with a fake probe/loader: switching games mid-test queues the new one; Offline retry tests after load; load failure -> LoadFailed; S: full flow |
| 18 | **Views extraction** (ViewServerList, ViewGameList, ViewCardHost, ViewInfoBar, ViewLockBar, ViewBoostBar, ViewTools, ViewHeader, ViewDev, ListStyle); MainForm becomes the shell | View*.cs, ListStyle.cs, MainForm.cs | S in both languages with screenshots compared before/after; dev page in Arabic (see 5.2) |
| 19 | **DetectService + CaptureGate + ZoneMemory** (manual, auto, lock test share the gate; `busy` no longer covers detect, fixes 5.1 #1 partly) | DetectService.cs | tests: gate is exclusive; LockChanged resets; NoMatchLong after 3 min of watched time (fake clock); S: Deadlock/Overwatch in a real match, lock test while auto is due |
| 20 | **LockService + FirewallWorker** (last: real firewall state). Also moves ResetEverything and the startup leftover-lock cleanup; `pendingRebuild` is applied whenever the app goes idle (fixes 5.1 #1, #3) | LockService.cs, FirewallWorker.cs, MainForm.cs | tests with a fake firewall: Lock then Unlock in quick order ends unlocked with Remove last; Drain runs Remove after an Apply in progress; S: lock/unlock fast, reset, close while locking, kill the process while locked -> next start removes the leftover rules |

Order logic: pure code first (steps 1-9, no runtime risk). Then services without system side effects (10-15), then the live
thread (16), then selection/test (17, which changes where `CurrentGame` comes from), then the views (18, RTL risk), then pktmon (19)
and the firewall (20), where a mistake leaves the player's PC in a bad state.

---

## 4. Dead code, duplication, leftovers

### 4.1 Delete

| Where | What | Why |
|---|---|---|
| src/GameDb.cs:52 | `Game.LockGroups` field | Leftover from "Play on it". Set at :304 and read nowhere |
| src/GameDb.cs:185-195 | `OwGroups` | Only feeds `LockGroups`. Replaced by `OwRegionMap` (:198-207) |
| src/GameDb.cs:220-225 | `Groups(params string[][])` | Only used by `OwGroups` |
| src/GameDb.cs:304 | `LockGroups = OwGroups,` in the Overwatch entry | same |
| src/MainForm.cs:1248 | `Short(string)` | Same as `ShortName` (:1016). Use one helper that takes a string |
| src/MainForm.cs:1754-1758 | `ValveRegionOf` | One-line wrapper over `g.DatacenterOf(ip)` (GameDb.cs:71), 7 call sites |
| src/MainForm.cs:3005 | `foreach (var g in games) Storage.SetSetting("tip_" + g.Icon, "")` | `tip_<icon>` keys are no longer written anywhere (removed in 8d2f524). It also does 4-12 extra settings.txt rewrites |
| src/MainForm.cs:2298-2300 | key name `tip_tray` and the "tray" wording | The tray was removed (83bcc96). Keep the key for compatibility but rename the comment |
| src/GameDb.cs:679-682 | `Storage.AddCustom` | No caller in src or tests |
| src/MainForm.cs:31 | comment "play here" tick box | Wording left over from PlayOn |
| README.en.md:39 (and the Arabic README if it has the same row) | "**Play on it** locks you to it" | That feature is gone; the card replaced it (spec). Belongs to the docs refresh in docs/RELEASING.md |
| Telemetry label "play on regions" (MainForm.cs:1401) | **Keep** | Dashboards group by it. Renaming would split the statistics |

### 4.2 Duplication to fold

| Copies | Fold into |
|---|---|
| `ShortName` MainForm.cs:1016 / `Short` :1248 | `Fmt.ShortName(string)` |
| `PingColor` MainForm.cs:1018 / `RegionTile.PingColor` StateCard.cs:364 (differ only in the "unknown" sentinel) | `Fmt.PingColor(double)` that treats `<0` and `MaxValue` as unknown, with a separate colour for each |
| Best region: MainForm.cs:1288, 1451, 1580, 2575 | `Routing.Best(...)` |
| List order `OrderBy(LockedOut ? 1 : 0).ThenBy(BestLatency)`: 1106, 1162, 1596 | `Routing.SortForList` |
| Unlock state reset: 2368-2371 and 2397-2400 | `LockService` (one place sets `Current = null`) |
| Datacenter from hits: 1806, 1811, 1847, 1861, 1887, 2058 and the two result handlers `ShowValveResult` (1882) / `ShowAutoDetect` (2106), which classify the same way | `MatchClassifier.Classify` |
| "Checking at your next match..." text: 2032, 2130 | one string in `ViewInfoBar` |
| "Is a game running": 170, 1632 | `GameSession.Running()` |
| Process priority loops: 2415-2417, 2601-2611 (neither disposes the `Process` objects) | `GamePriority.Set(Game, ProcessPriorityClass)` with `using` |
| Dev button factories: 2751, 2779, 2802 | one `DevButton(text, width, act)` in `ViewDev` |
| Dialog boilerplate (FixedDialog, colours, font, RTL): InputDialog 3015-3025, SettingsDialog Settings.cs:35-45, WelcomeDialog Telemetry.cs:180-189, UpdateDialog Update.cs:185-195 | `Dialogs.Themed(string title)` factory |
| `Brand.GameIcon` / `Brand.GameIconResource` (Brand.cs:57-79) | one cached resource loader |
| Deadlock AppId `"1422450"`: MainForm.cs:1125, 1255, 1459, 1717 | `Game.HasMatchActivity` (or `g.Icon == "deadlock"` in one place) |
| Lock failure message built twice (2317, 2319); boost failure (2505, 2507) | `notices` + `log` in the controller handler |

### 4.3 Misplaced types (no code change, just a file move)

`Lang` and `Storage` live in GameDb.cs. `Zones` lives in RegionGroups.cs. `ServerLock` lives in Tunnel.cs. `Detector` lives in Stats.cs.
`WelcomeDialog` and `DevPanel` live in Telemetry.cs. `InputDialog` and `PingGraph` live in MainForm.cs. `LockScope` (MainForm.cs:2845)
belongs with `LockTest.LockInfo`. Theme colours live on `MainForm`, which makes `CardLogic` (a pure class) depend on the form.

---

## 5. Risks

### 5.1 Bugs found while mapping (fix during the matching step)

1. **Settings changes are lost while a detect or reset is running.** `OpenSettings` (920) and `SetDevMode` (2739) defer to
   `pendingRebuild` whenever `busy` is set. But `busy` is also set by `DetectServer` (1833) and `ResetEverything` (2408), and only the
   end of `RunTest` (1567) applies `pendingRebuild`. If the player changes language or layout during a 15 s server check, nothing
   happens until a later ping test. Fix: apply it in one "went idle" hook (steps 19-20).
2. **Pressing Stop while the relay route is being built leaves the tunnel up.** `tunnelActive = true` is set on the background thread
   after `Tunnel.Start` (2498). If `StopAll` runs before that line (`btnStop` is enabled at 2463), it sees `false` and doesn't stop the tunnel.
   The task then posts `StartMonitor` (2501) with `boosting == false`. Fix: `BoostService.Building` plus a cancel flag checked
   after Start, with the tunnel stop queued on the same `FirewallWorker` (step 16).
3. **Firewall operations race each other.** `ServerLock.Apply` (2271, Task), `ServerLock.Remove` in `UnlockServers` (2381, Task) and
   `ResetEverything` (2412, Task) can interleave. `btnUnlock` stays enabled while a new lock is applied over an old one (2212). And
   `FormClosing` (159) can remove the rules *before* an Apply still in flight writes its own, which leaves rules after exit (the next start
   cleans them as `leftover_lock`). Fix: `FirewallWorker` FIFO + `Drain` in teardown (step 20).
4. **settings.txt read-modify-write without a lock, from several threads.** Telemetry.EnsureLocation runs on the thread pool for every
   event (Telemetry.cs:104-108), along with `InstallId`, `Updater.FirstSeen`, `SystemInfo.FindExe` (self-test runs it on a Task) and the UI thread
   (`last_game`, zones, tips). Two concurrent `SetSetting` calls can drop each other's key. It is also slow: every `GetSetting`
   re-reads and re-parses the file, and `Telemetry.Send` reads it at least 4 times per event (HasConsent ×2, geo_at, Prefs.Simple, InstallId,
   Nickname). Fix: step 4.
5. **RebuildUi wipes the match state.** Re-selecting the game (962) fires `OnGameChanged`, which runs `ClearMatch` (1068),
   `ResetDetectButton` and possibly an auto test. After saving Settings in the middle of a match, the card drops "You're playing in ..." until
   the next automatic check (up to 90 s). With `GameSession.Select` the controller can skip the reset when the game is the same.
6. **`monitorGen` is read on the monitor thread without `volatile`** (2522, 2528, 2531). It works by luck on x64. Use `Interlocked`/`volatile`
   in `LiveMonitor`.
7. Minor: during the 15 s manual check `RunTest` returns silently (1508-1513), so "Test ping" does nothing and nothing is queued.
   `Detector.IsRunning`/`PortsOf` and the priority loops never dispose their `Process[]`. In an app that sits minimized for days, every 10 s
   tick leaves handles for the finalizer. `FlushDns` (2701) blocks the UI thread for up to 10 s.

### 5.2 RTL mirroring

- Arabic mirroring is a single pass: `MirrorChildren` (474-491) runs once over the finished form (458). **Any control added after that
  pass is not mirrored** (dock sides, absolute `Left`). View extraction must build the whole tree and *then* mirror once. Alternatively each view
  mirrors itself in `OnCreateControl`, but never both, or the docks flip back.
- The skip list (ListView/ListBox/ComboBox/TextBox/StateCard) has to stay exact. `StateCard`, `RegionTile`, `RoundButton`, `Chip` and `SoftControl`
  depend on `IsMirrored` (`LeadRight = rtl && !IsMirrored`). Re-parenting them under a different container type changes `IsMirrored` and
  draws them on the wrong side.
- The ListView stays LTR and reverses `DisplayIndex` (714-715). Tick-box hit testing (556) and `FirstCell` (778) depend on that. Keep them
  together in `ViewServerList`.
- The developer page sets `RightToLeft.No`, but `MirrorChildren` still flips its docks (it isn't in the skip list), so in Arabic the tools
  card sits on the left. Keep that unless it is changed on purpose. Compare screenshots before and after step 18.
- `ChatToasts` and `ToastForm` read `Lang.Ar` live. `Lang.Ar` is a global static changed by SettingsDialog. A view that caches strings
  must be rebuilt (it is, via RebuildUi).

### 5.3 Developer mode

- `GameDb.ShowUnreleased` is a global static that `MainForm` (86, 947) and `SettingsDialog`/`WelcomeDialog` (via `GameDb.Available`) read.
  Moving it into `GameSession.DevMode` means the dialogs must get the list passed in, or the global must stay in sync until they do.
- Tab index 3 is hard-coded (2737-2738, 2761, 2857, 2909, 2938, 2973). Name it (`Pages.Dev`) before pages move into views.
- The lock test holds pktmon for 60 s and must go through `CaptureGate`. FormClosing calls `Detector.Cleanup` only when `lockTesting` (160).
  Keep that, and never clean up a capture that isn't ours (startup check at 117).
- The `Issues.Reported` handler (89-93) is static and is never removed. That's fine for one form, but the controller must not subscribe again on RebuildUi.

### 5.4 Settings change / RebuildUi

- RebuildUi disposes every control and builds them again. Today the background callbacks survive because they read *fields* when they
  run (`graph`, `liveLabel`, `devOut`...). After the refactor, services must never keep control references. Only the controller holds views,
  and it swaps them in `AttachViews`.
- State that has to survive a rebuild: the log (`AppLog`), `manualOpen`/`expanded` (now in `ChoiceSet`), the route box selection,
  `boosting` (960-961), the update bar (958), the current page, and the lock and match (see 5.1 #5).
- `pendingRebuild` must become "rebuild when idle", where idle means no ping test, no manual check, no reset and no lock being applied.

### 5.5 Self-update

- `Updater.Install` starts the new exe *before* this one closes (Update.cs:105). The new process waits up to 20 s for this PID (`FinishUpdate`)
  and then takes the mutex. So `FormClosing` teardown has to stay **synchronous and under ~15 s** (netsh Remove, WireGuard stop,
  `Telemetry.Exit` 3 s). Don't move teardown onto a Task that the process doesn't wait for.
- `CloseForUpdate` sets `closingForUpdate` so the update prompts stop. It has to stay readable from `UpdateService` before `Close()`.
- `Updater.Forced` → `DaysLeft` → `FirstSeen` *writes* settings (Update.cs:72) when the bar text is built. Keep it on one thread (SettingsStore fixes the race).

### 5.6 Single instance / BringUp / teardown

- `Program` holds a reference to the one `MainForm` and posts `BringUp` from its listener thread (Program.cs:57). Keep `MainForm` as the
  long-lived window and never recreate it on a Settings change. `BringUp` must stay public and safe to call while minimized (it calls `RestoreWindow`).
- Teardown order (FormClosing 155-162), which must stay the same in `MainController.Teardown`: stop the live monitor, stop the tunnel, remove the lock
  rules (after `FirewallWorker.Drain`), clean up our pktmon capture if a lock test runs, send `Telemetry.Exit`.

### 5.7 Threading model changes

- Today state is only touched on the UI thread, with a few exceptions: `lockSummary` read on a Task (1849), `appActive` writing `lastInFront`
  from the monitor thread (225), and `r.Direct`/`r.ViaRelay` written by `Parallel.ForEach` while the UI may read them. The target keeps
  that model on purpose. Moving to locks would add more risk than it removes.
- `Region` objects are shared and mutable (`Picked`, `Here`, `Direct`, `BestEndpoint`, `ViaRelay`). The ping test writes them from up to 64 pool
  threads. That is acceptable while only reference assignments happen, so keep it that way: never mutate collections inside a `Region` from a
  background thread, and assign a fresh `Dictionary` the way 1553 does.
- The pktmon captures (8-60 s of `Thread.Sleep`) run on pool threads, which competes with the ping test's 64 minimum threads. Use `Background.RunLong`.

### 5.8 Testing gaps

- `Storage.Dir` points at the player's real `%APPDATA%\2XPing`. Tests that touch settings, picks or relays need `SettingsStore.UseDir` /
  `Storage` path injection first (step 4), or they will overwrite the developer's own settings when run.
- Tests call `MainForm.ServerHits` and `MainForm.TitleFor` (tests/ZoneTests.cs:168-184). Repoint them when those move (steps 8 and 18).
- Views have no automated tests. The spec's "harness with fake states" screenshot tool would cover step 18. Until it exists, the
  manual **S** pass with before/after screenshots is the proof.
