# Simple Main Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the busy centre of the main window with one state card that offers 2-3 region choices (ping, queue, players), locks them in one press, and then says in plain words whether the lock works.

**Architecture:** Three new focused files: `RegionGroups.cs` (pure data and ranking: datacenters → regions → featured choices), `CardLogic.cs` (pure: app situation → what the card says), `StateCard.cs` (the WinForms control that draws it). `MainForm.cs` only feeds them and reacts to their events. The lock, ping test and automatic check underneath stay as they are.

**Tech Stack:** C# 5 on .NET Framework 4.8 WinForms, compiled with the Windows `csc.exe` by `build.ps1`; no test framework, so tests are a console exe built by `tests/run-tests.ps1`.

**Spec:** `docs/superpowers/specs/2026-10-13-simple-main-screen-design.md`

## Global Constraints

- C# 5 only: no `$""`, no `?.`, no expression-bodied members (see CONTRIBUTING.md).
- Every user-facing string is bilingual: `T("English", "عربي")` / `Lang.T(...)`; Arabic is natural Hijazi, as in the spec's tables (copy those strings verbatim).
- Colours and controls come from `Ui.cs` (`Card`, `RoundButton`, `SoftControl`) and `MainForm` colour fields (`Accent`, `Panel1`, `Panel2`, `TextC`, `Dim`, `Good`, `Warn`, `Bad`, `Lavender`).
- RTL: the first item sits on the reading side; use `LeadRight` in `SoftControl` subclasses as `Toggle`/`GameTile` do.
- Simple layout must not show the words relay/وسيط, blocked/محجوب, `ms` other than one ping number, or datacenter codes.
- Only datacenters that host matches (`Game.Hosts(r)`) are ever offered or locked through a region.

## Review Focus

- Before the first ping test finishes, regions have no ping: tiles must show «لحظة...» instead of a number and the button stays hidden, never «بنق 0».
- Every region unreachable (no internet, firewall): the card must go to the No-internet state, not show three tiles with no ping.
- Deadlock player data missing (deadlock-api down): tiles show ping only and the tag falls back to «أقل بنق»; no «أسرع قيم» on a guess.
- A manual lock that mixes datacenters from several regions: the Locked title lists the picked server names, not a wrong region name.
- The game list switches while a test is running: the card must show the newly picked game, never the previous game's tiles.

---

### Task 1: Test runner and region grouping

**Files:**
- Create: `src/RegionGroups.cs`
- Create: `tests/RegionTests.cs`, `tests/run-tests.ps1`
- Modify: `src/GameDb.cs` (add `RegionMap` to `Game`; fill it for Overwatch 2 and Dead by Daylight)

**Interfaces:**
- Produces:
  - `Game.RegionMap : List<RegionDef>` (null = no regions, e.g. Arena Breakout; Deadlock builds it from `Stats.PopToMm` at load).
  - `class RegionDef { public string Key, En, Ar; public string[] Ids; }`
  - `class RegionChoice { public RegionDef Def; public List<Region> Members; public double Ping /* double.MaxValue = unknown */; public int Matches /* -1 */; public int Players /* -1 */; public string Tag /* "best","ping","queue", null */; public bool Selected; public string Name { get; } }`
  - `static List<RegionChoice> RegionGroups.Build(Game g, Dictionary<string,int> activity, Func<int,int> playersOf)` — one choice per `RegionDef` that has at least one hosting member; ping = best member `BestLatency`.
  - `static List<RegionChoice> RegionGroups.Featured(List<RegionChoice> all)` — at most 3, rules below, `Tag` set, first one `Selected = true`.
  - `static string RegionGroups.Busy(int players)` → `Lang.T("lots of players","لاعبين كثير")` ≥ 10000, `("some players","لاعبين متوسط")` ≥ 2000, else `("few players","لاعبين قليل")`.

- [ ] **Step 1: Write `tests/run-tests.ps1`** compiling every `src/*.cs` except `Program.cs` plus `tests/*.cs` into `tests/bin/tests.exe` (/target:exe, same `/r:` list as `C:\Temp\perf\build-shots.ps1`), running it, and exiting with its exit code. `tests/bin/` goes in `.gitignore`.

- [ ] **Step 2: Write the failing tests in `tests/RegionTests.cs`** (a `static class Tests` with `Main` that runs each and returns the failure count):
  - `OverwatchRegionsCoverEveryDatacenterOnce`: load Overwatch 2 (`GameDb.Load`), assert every region id appears in exactly one `RegionDef`, except ORD1 which is in North America only (spec table).
  - `DeadlockRegionsSkipRelayOnly`: load Deadlock; no `RegionDef.Ids` contains `dxb` or `jnb`; Europe contains `fra` and `par`.
  - `DbdRegionsMatchSpec`: Europe = eu-central-1, eu-west-1, eu-west-2; North America = us-east-1, us-east-2, ca-central-1, us-west-1, us-west-2.
  - `FeaturedPicksBestPingAndFastestQueue`: synthetic game with regions A(ping 78, matches 120), B(160, 200), C(110, 15) → Featured = [A tag "ping", B tag "queue", C tag null]; A.Selected.
  - `FeaturedCollapsesWhenOneWinsBoth`: A(78,200), B(160,120) → Featured[0] = A tag "best"; count 2.
  - `FeaturedWithoutQueueData`: all Matches = -1 → tags only "ping" on the first; never "queue".
  - `UnknownPingSortsLast`: A(MaxValue), B(90) → Featured[0] = B.

- [ ] **Step 3: Run** `powershell -ExecutionPolicy Bypass -File tests/run-tests.ps1` — Expected: compile error (RegionGroups missing).

- [ ] **Step 4: Implement** `RegionDef`, `RegionChoice`, `RegionGroups` and the `RegionMap` data (spec "Regions per game" lists, names from the spec). Featured rule: sort by ping; take best ping; take the region with most `Matches` if ≥ 0 and different; fill to 3 with the next best ping; tag "best" when the best-ping region is also the most-matches one.

- [ ] **Step 5: Run tests** — Expected: `ALL PASS`, exit 0.

- [ ] **Step 6: Commit** `git add src/RegionGroups.cs src/GameDb.cs tests .gitignore` — "Region groups and featured choices, with a test runner".

### Task 2: Lock by region

**Files:**
- Modify: `src/RegionGroups.cs`, `src/MainForm.cs` (`PlayOn` area)
- Test: `tests/RegionTests.cs`

**Interfaces:**
- Consumes: `RegionChoice`, `Game.Hosts`.
- Produces: `static List<string> RegionGroups.PickIds(Game g, IEnumerable<RegionChoice> chosen)` (hosting members only, no duplicates); `void MainForm.LockRegions(List<RegionChoice> chosen)` — sets `Picked` from `PickIds`, `Storage.SavePicks(g.LockKey, ids)`, refreshes, calls the existing `LockServers()`.

- [ ] **Step 1: Failing test** `TwoRegionsLockExactlyTheirDatacenters`: Deadlock loaded; choose Europe + Russia → `PickIds` equals the union of their hosting ids; contains no North America id and no `dxb`.
- [ ] **Step 2: Run** — Expected: FAIL (PickIds missing).
- [ ] **Step 3: Implement** `PickIds` and `LockRegions`; `PlayOn(Region)` becomes `LockRegions` of the region containing it (keeps Overwatch's whole-region behaviour).
- [ ] **Step 4: Run** — Expected: ALL PASS.
- [ ] **Step 5: Commit** — "Lock whole regions from the card".

### Task 3: Card logic

**Files:**
- Create: `src/CardLogic.cs`
- Test: `tests/CardTests.cs`

**Interfaces:**
- Produces:
  - `enum CardKind { Loading, Testing, Choose, Locked, InMatchOk, InMatchRelay, Outside, BestOnly, OnServer, Offline }`
  - `class CardInput { public string Game; public bool Loaded, LoadFailed, Testing, CanLock; public List<RegionChoice> Choices; public string LockedNames; /* null = not locked */ public string MatchRegion; public bool MatchOnPicked, MatchViaRelay; public string BestServer, CurrentServer; public int BestMs = -1, CurrentMs = -1; }`
  - `class CardView { public CardKind Kind; public string Title, Sub, Button; public bool ShowTiles, ShowLinks; public Color Accent; }`
  - `static CardView CardLogic.Decide(CardInput i)` — the spec's state table, strings verbatim.

- [ ] **Step 1: Failing tests**, one per spec row, asserting `Kind` and `Title` (Arabic, `Lang.Ar = true`), e.g.:
  - not loaded → Loading, «أجيب سيرفرات Deadlock...»
  - loaded, testing, no ping yet → Testing, «أدوّر لك أحسن منطقة...»
  - lockable, choices with ping, not locked → Choose, `ShowTiles`, Button «العب على أوروبا»
  - two selected → Button «العب على أوروبا وروسيا», Sub «البنق بين 78 و110، والبحث أسرع»
  - locked, no match → Locked, «✓ مثبّت على أوروبا», Sub contains «جرّب مباراة عادية أول»
  - locked, match on picked → InMatchOk, «تلعب الحين في أوروبا ✓»
  - locked, match via relay → InMatchRelay, «تلعب الحين في واحد من مناطقك ✓ (أوروبا)»
  - locked, match outside → Outside, «اللعبة حطّتك على أمريكا الشمالية», `Accent == MainForm.Warn`
  - not lockable, no match → BestOnly, «أحسن سيرفر لك: السعودية · بنقك تقريباً 30»
  - not lockable, in match → OnServer, «إنت الحين على: السعودية · بنقك 32»
  - load failed, or every choice ping unknown after a finished test → Offline, Button «جرّب مرة ثانية»
  - Review Focus: choices exist but none has ping and not testing → Offline (never tiles with no ping).
- [ ] **Step 2: Run** — Expected: compile failure.
- [ ] **Step 3: Implement** `CardLogic.Decide`; `Accent` = `Good` for locked/in-match OK states, `Warn` for Outside, `Accent` otherwise.
- [ ] **Step 4: Run** — Expected: ALL PASS.
- [ ] **Step 5: Commit** — "Card logic: the app's situation in plain words".

### Task 4: The state card control

**Files:**
- Create: `src/StateCard.cs`
- Modify: `C:\Temp\perf\Shot.cs` harness gets a `card:<kind>` mode (outside the repo; local only)

**Interfaces:**
- Consumes: `CardView`, `RegionChoice`, `RegionGroups.Busy`, `Ui.cs` controls.
- Produces: `class StateCard : Card` with `void Show(CardView v, List<RegionChoice> tiles)`, events `EventHandler ButtonPressed`, `EventHandler OtherRegions`, `EventHandler Manual`, `EventHandler SelectionChanged`; `class RegionTile : SoftControl` (toggle; shows name, tag pill, «بنق {ms}» or «لحظة...», wait and «{Busy} · {Approx(players)}» when known).

- [ ] **Step 1:** Implement the control: title (Semi 16), sub (Ui 10, Dim), up to 3 `RegionTile` in a row (first on the reading side), one `RoundButton` (Accent fill, black text), two link labels «مناطق ثانية» / «اختيار يدوي» (Lavender). Selecting tiles raises `SelectionChanged` so `MainForm` re-runs `Decide` for the button text.
- [ ] **Step 2:** Harness: `shot.exe ar card:<Kind> out.png` renders a 760×300 `StateCard` with sample data for each `CardKind`; capture all 10 in Arabic and English.
- [ ] **Step 3:** Look at every screenshot: nothing clipped, RTL order right, one ping number, no jargon. Fix and recapture until clean.
- [ ] **Step 4: Commit** `src/StateCard.cs` — "State card control".

### Task 5: Main window wiring

**Files:**
- Modify: `src/MainForm.cs` (`BuildUi`, `BuildServersPage`, `BuildHero` removed, `UpdateHero` → `UpdateCard`, detect handlers, `RunTest`, `OnGameChanged`, lock/unlock)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: `void UpdateCard()` called wherever `UpdateHero()` was, plus after detect results, lock/unlock, test start/end and game change; fields `string matchRegion; bool matchOnPicked, matchViaRelay; string currentServer; int currentMs` set by `ShowAutoDetect`/`ShowValveResult`/`ShowOtherResult` and cleared by `LockChanged` and game change.

- [ ] **Step 1:** Layout. Simple: header (logo, Settings, Contact; no status chip), game list, the `StateCard` filling the centre; the list card (lock bar + server list) hidden until «اختيار يدوي», then shown under the card; no info bar, no bottom status line. Advanced: card on top, info bar, tabs, list, graph as today. «مناطق ثانية» shows all `Build(...)` choices as tiles in a second row (wrap), not a new window.
- [ ] **Step 2:** Wiring. `ButtonPressed` → by `Kind`: Choose → `LockRegions(selected)`; Locked/InMatch*/Outside → `UnlockServers()`; Offline → reload the game. Detect results feed the fields above; the auto-detect relay branch sets `matchViaRelay`. Arena Breakout's automatic check sets `currentServer`/`currentMs` from `Stats.Locate` and a ping.
- [ ] **Step 3:** Review Focus: `OnGameChanged` clears `matchRegion`/`currentServer` and calls `UpdateCard()` before loading; a test finishing for a game that is no longer selected doesn't touch the card.
- [ ] **Step 4:** Build (`build.ps1`), run `tests/run-tests.ps1`, capture the main window (mainshot) for the four games in both languages; check against the spec tables.
- [ ] **Step 5: Commit** — "Main window: one state card in the simple layout".

### Task 6: Fewer notices

**Files:**
- Modify: `src/MainForm.cs` (`Msg` calls)

- [ ] **Step 1:** In the simple layout, drop the `Msg` calls whose text the card now carries: lock done («تم التثبيت...»), «جرّبه في مباراة عادية أول...», «لو اللعبة مفتوحة، اقفلها...», detect results («إنت تلعب دحين على...», «هادا من السيرفرات اللي اخترتها...»), «تم فك التثبيت». Keep: firewall off, lock rules removed, update, errors, the longer-route warning, exe not found. Advanced layout keeps all.
- [ ] **Step 2:** Build, lock/unlock in the harness, confirm only the kept notices appear.
- [ ] **Step 3: Commit** — "Simple layout: notices only for what the card can't say".

### Task 7: Changelog and hand-off

- [ ] **Step 1:** CHANGELOG "Next" entry for the new screen; README/guide/slides are refreshed at release time (docs/RELEASING.md).
- [ ] **Step 2:** Send the user the screenshots and the build; ask for the real-play check from the spec's Testing list.
- [ ] **Step 3: Commit** — "Changelog: simple main screen".
