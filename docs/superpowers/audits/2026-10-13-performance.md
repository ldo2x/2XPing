# 2X Ping performance and network audit (branch simple-main-screen, 3eee989)

Read-only on the repo. Measured with a scratchpad copy of src (Telemetry URL pointed at a local listener, data folder redirected) and non-elevated harnesses in `scratchpad\perf` (Run.cs, Run2.cs, Perf.cs, Perf2.cs, Srv.cs). Machine: Ryzen 9 7900X, 1 Gbit ethernet, Windows 11.

## 0. Incident to know about

`app.manifest` is `requireAdministrator`. My first test launched a copy of the app (`scratchpad\perf\app.exe`, PID 45688, started 15:45) and it elevated. It used the real data folder `%APPDATA%\2XPing`, not the redirected one, because the elevated process did not inherit my env var. Effects:

- It rewrote `settings.txt` (same keys, nickname intact) and `last.txt` (fresh Deadlock ping results).
- It sent telemetry only to `127.0.0.1:18080` (my local listener), never to Supabase.
- The elevated process cannot be killed from my non-elevated shell, and CloseMainWindow had no effect (UIPI).
- **Please close the "2X Ping (Beta)" window that was opened at 15:45, or end `app.exe` in `scratchpad\perf` from Task Manager.**
- All later measurements used non-elevated in-process harnesses with a private data folder. Nothing was written to the real folder.

## 1. Measured numbers

| What | Result |
|---|---|
| Startup, process start to window shown | 299 ms (fonts 4 ms, `MainForm` ctor 109 ms). Telemetry `startup_ms` in the first run read 262 ms |
| First 30 s (startup, load Deadlock, auto ping test, update check, stats) | about 1.3 s CPU in total |
| Idle CPU, window open, nothing running | 3.9 Mcycles/s = 0.08% of one 4.7 GHz core (151 s, exact `QueryProcessCycleTime`) |
| Idle CPU, minimized | 0.9 Mcycles/s = 0.02% of one core |
| Memory, idle | Working set 76-78 MB, private 45 MB, managed heap 2 MB after GC |
| GDI objects | 91 at baseline, 94 after 8 game switches and 24 toasts, then flat (no leak) |
| USER objects | 86 at start, 60-62 steady |
| Handles | about 600 steady (about 1000 during startup and test, then released) |
| Threads | 82 during startup and test, 9-14 idle |
| Full Deadlock ping test | 32 regions, 63 endpoints (ICMP), 3 echoes each. Wall 1.9-2.2 s, CPU 0.5-0.65 s, peak 84 threads, 63 probes in flight, about 890 handles |
| Same test, lower parallelism | MDOP 16: 1.96-1.98 s wall (no slower). MDOP 8: 3.6-3.7 s. Peak threads stay at 84 in every case |
| Other games | Overwatch 2: load 0.8 s, test 2.0-3.3 s, 16 probes. DBD: load 1.1 s, test 1.3-1.9 s. Arena Breakout: test 1.1-1.8 s |
| `Storage.GetSetting` | 0.073 ms per call (the whole file is re-read every call) |
| `Fonts.Ui` create and dispose | 0.002 ms |
| `Process.GetProcessesByName` | 11 ms per call. `PortsOf(Noisy)` = 19 calls = 148 ms |
| Process spawns | `netstat -ano -p UDP` 23 ms, `pktmon` 16 ms, `netsh show rule` 48 ms, `netsh show state` 42 ms |

The first harness sampled every second and showed about 8 ms/s of CPU. That was almost all the sampler's own overhead (`Process.Refresh`), so ignore it. The cycle-counter figures above are the real idle cost.

### Wire usage per activity

Measured with curl and the loopback listener. TLS and TCP overhead is estimated.

| Activity | When | Size | Per hour / day |
|---|---|---|---|
| Telemetry heartbeat | Every 60 s while the app is open, including minimized and idle (`Telemetry.cs:61`) | Request 219 B headers + 326 B body = 545 B. Response about 1.0 KB (Supabase header size). About 1.8 KB per beat with TCP/TLS overhead. The TCP/TLS connection is reused between beats (verified: one connection for 4+ minutes) | about 108 KB/h, about 2.6 MB per 24 h open. **Largest steady, always-on stream** |
| Telemetry events (`start`, `test`, `use`, `detect`, `exit`) | On actions | `start` 865 B body, `test` 951 B, `use` 327 B | a few KB per session |
| Update check | At launch and every 3 h (`MainForm.cs:151`) | GitHub `releases?per_page=10`: 4.7 KB gzip body + 1.3 KB headers + TLS handshake about 4-5 KB = about 11 KB | about 88 KB/day |
| Player stats (Deadlock) | Every 5 min while the window is in front (`MainForm.cs:2000`) | Supabase `game_stats` GET: 212 B request + 1.2 KB response | about 18 KB/h |
| Deadlock fallback (Supabase stats missing or older than 10 min) | Per refresh | deadlock-api `/v1/info` 1.1 KB + 0.8 KB headers, plus `/v1/sql` | adds a few KB |
| Auto-detect | Every 90 s (30 s for 10 min after a lock change), only while our window is in front and the game is running | Valve games: 0 network. Games without a datacenter list (Arena Breakout, custom): one `ip-api.com` call per check, about 0.8 KB | up to 32 KB/h (120 calls/h in the verifying window) |
| Full Deadlock ping test | Per game per session, plus manual Test | ICMP: 189 echoes x about 120 B = 23 KB. Plus Steam SDR config 7.6 KB gzip + 0.3 KB headers + TLS handshake 5 KB, plus 2.5 KB telemetry | about 38 KB per test (first load) |
| Overwatch test (estimate) | Per test | About 12 HTTPS endpoints, each with a full TLS handshake (3-5 KB) + 4 requests | about 60-90 KB |
| Live ping monitor (locked or boosting) | 1 probe/s with the window in front, 1 per 15 s otherwise (`MainForm.cs:2525`) | ICMP about 130-150 B per probe | about 500 KB/h in front, about 33 KB/h otherwise |
| Daily geo lookup | Once per UTC day (`Telemetry.cs:84`) | ip-api JSON | negligible |
| AWS ranges for AWS games | At most once per 24 h per file (`IpLists.cs:21`) | **2.72 MB** (AWS sends it uncompressed), plus Google Cloud 5.3 KB gzip and RIPE 1.4 KB | only for locked AWS or Blizzard games |

Everything polling-like pauses when the window is not in front (stats, auto-detect, live ping drops to 1 per 15 s). That design is good. The network floor with the app minimized is the heartbeat plus the 3 h update check, about 2.7 MB/day.

## 2. Ranked improvements

Ranked by value to the owner's goals (less internet, less CPU, never touch game ping). Risk is for the code change.

### 1. Parse the AWS ranges once, not once per region (`IpLists.cs:63-83`, callers `GameDb.cs:387` and `MainForm.cs:2253-2256`)

- **Problem:** `Resolve("aws:<region>")` reads the 2.7 MB file and runs `JavaScriptSerializer` on it for every region. Dead by Daylight loads 15 regions, so Load takes 1.1 s of CPU (measured 1123 ms for 15 resolves, about 75-80 ms each). Lock repeats the 15 resolves (1.13 s measured) and Boost repeats them again (`AwsCidrs`).
- **Fix:** cache the parsed prefix list in a static variable keyed by file mtime, or group the prefixes by region on first parse. Do the same for the `gcp` and `asn` files.
- **Saving:** about 1 s CPU per AWS-game load, about 1 s per lock, and about 1 s per boost. Load goes from 1.1 s to about 0.1 s.
- **Risk:** very low.

### 2. Fewer process enumerations and spawns per auto-detect capture (`Stats.cs:206-267`)

- **Problem:** per capture it does `PortsOf(processNames)` twice and `PortsOf(Noisy)` twice. The noisy pass is 19 `GetProcessesByName` calls (about 148 ms each time) plus a `netstat` spawn. It is never used when the game is known (`knownGame ? ... : noisyPorts.Contains`, `Stats.cs:283`). On top of that `IsRunning` (`MainForm.cs:2051`) enumerates processes again.
- **Fix:** skip the noisy passes when `processNames.Length > 0`. Use one `Process.GetProcesses()` snapshot for all name lookups, then group by name.
- **Saving:** about 0.35-0.5 s CPU and 2 `netstat` spawns per capture. During the 10-minute "verifying" cadence (every 30 s) that is about 1% of a core and 20 fewer spawns.
- **Risk:** low.

### 3. Narrow the pktmon capture (`Stats.cs:247-259`)

- **Problem:** `pktmon filter add 2XPingUDP -t UDP` copies the first 96 bytes of every UDP packet on every NIC (QUIC browsing, Discord, video, the game) for 8 s, writes an `.etl`, then converts the whole thing to text and regex-parses every line. It is the only component that sits in the packet path while a game is running, and I could not measure it (needs admin, which I did not have).
- **Fix:** add one filter per local port the game currently owns (`pktmon filter add n -t UDP -p <port>`, `gamePorts` is already computed before capture). Fall back to all UDP only when the port list is empty.
- **Saving:** capture volume, the etl2txt time and the file I/O drop by orders of magnitude on busy networks. It also reduces any chance of touching the game's packets.
- **Risk:** medium. Behaviour changes if the game opens new sockets mid-capture, and the filter syntax needs a test with admin.

### 4. Cap ping-test parallelism and skip the auto test while the game is running (`MainForm.cs:1532`, `Program.cs:42`, `MainForm.cs:1113`)

- **Problem:** a Deadlock test fires 63 probes at once (peak 84 threads) as soon as a game is selected, even if the player is mid-queue or mid-match.
- **Fix:** use MDOP 16 for the regions. It took the same 1.96 s wall time as 64, with half the concurrent probes (about 32 instead of 63). Also skip the automatic test when the game process is running, so only the manual Test button runs it.
- **Saving:** smaller upload burst, a little less CPU. No bytes saved.
- **Risk:** low. The 64 min threads setting is harmless.
- **Bigger option:** an async rewrite of `Pinger` (`SendPingAsync`, async connect) would drop the test to about 5 threads and less than 0.1 s CPU, at medium risk. The thread count did not drop with lower MDOP because PLINQ inside `Parallel.ForEach` blocks pool threads.

### 5. Heartbeat: halve or gate it (`Telemetry.cs:61`)

- **Problem:** 108 KB/h, about 2.6 MB/day, even when minimized with no game running.
- **Fix, option A:** 120 s interval with the developer panel's "online now" window changed from 2.5 min to 4.5 min (`Telemetry.cs:446`). Saves about 1.3 MB/day.
- **Fix, option B:** send only while the selected game's process is running or the window is in front, plus one `idle` event on change.
- **Saving:** 50-90% of the app's steady upload.
- **Risk:** low code risk, but it changes what the developer view means. This is the owner's call.

### 6. Update check: conditional request and a smaller page (`Update.cs:36`)

- **Fix:** use `?per_page=3` (releases are not paginated by size, only count). Store the response `ETag` and send `If-None-Match`; a 304 costs about 0.5 KB, does not count against GitHub's 60 requests per hour limit, and skips JSON parsing. Add `If-None-Match` support in `Http.Get`.
- **Saving:** about 11 KB to about 2 KB per check, about 88 KB/day to about 16 KB/day. It also avoids silent failures when many users share one NAT IP and hit the unauthenticated rate limit.
- **Risk:** low.

### 7. Cache `Stats.Locate` per IP (`Stats.cs:168`, call at `MainForm.cs:2058`)

- **Problem:** for games with no datacenter list, every auto-detect check (90 s, 30 s while verifying) makes a fresh `ip-api.com` request for the same server. ip-api's free limit is 45 requests per minute per IP.
- **Fix:** a static `Dictionary<string,string>` keyed by IP, with an hourly expiry.
- **Saving:** about 30 KB/h while such a game runs with the window in front, plus one thread-pool hit per check.
- **Risk:** very low.

### 8. `EnsureLocation` retries every post when ip-api fails (`Telemetry.cs:84-111`)

- **Problem:** `geo_at` is only written on success, so if ip-api is blocked or rate-limited, each telemetry post (every 60 s heartbeat) first makes a 6 s-timeout HTTP call.
- **Fix:** remember the failure for an hour (a static `DateTime`).
- **Saving:** up to 60 wasted requests per hour and a blocked pool thread.
- **Risk:** very low.

### 9. Lock-rule check runs even when the game is in front (`MainForm.cs:2088-2095`)

- **Problem:** `CheckLockRules` runs inside the 10 s tick before the `appActive` check, so `netsh advfirewall firewall show rule` (48 ms with no rules, longer with a few hundred ranges) spawns every 2 min during a match.
- **Fix:** query the firewall through `INetFwPolicy2` (COM, no process), or make it run only when `appActive`, or space it at 5 min.
- **Saving:** 30 process spawns per hour, small CPU.
- **Risk:** low (COM) or very low (gating).

### 10. Live ping monitor (`MainForm.cs:2519-2543`)

- **Fix:** the in-front rate of 1 probe/s costs about 500 KB/h. Use 2 s while the window is in front. Replace the `Sleep(500)` polling loop used while not in front with a wait on a signal or a plain `Sleep(15000)`, so the thread does not wake twice a second.
- **Saving:** about 250 KB/h in front, and one wake-up per second removed.
- **Risk:** low. Changes graph resolution.

### 11. Smaller items

- `ServicePointManager.Expect100Continue = false` and `UseNagleAlgorithm = false` (`Program.cs:40`): removes the Expect-100 round trip and the up-to-350 ms wait on small POSTs. Latency only, a few bytes saved.
- `Http.Send` (`Net.cs:168`) has no `AutomaticDecompression`, so the developer panel's 5000-row `installs` query (`Telemetry.cs:431`) comes uncompressed. Dev-only, about 5-10x saving with gzip.
- `Pinger.dns` (`Net.cs:45`) never expires, so a relay or CDN IP change is never noticed in a long session (correctness more than performance).
- `Pinger.Measure` sleeps 30 ms between echoes (`Net.cs:151`), so each region takes 3 round trips plus 60 ms; ok.
- `GameDb.Load` for Overwatch (`GameDb.cs:234`) downloads `gcping/endpoints` (5.3 KB) on every load; it could be cached with the 24 h file cache.
- `Storage.SetSetting` is read-modify-write from several threads (Telemetry geo values vs the UI), which can lose an update. Cheap fix: a lock plus a small in-memory cache with mtime check. Not a performance issue, so only noted.

## 3. Already fine

- **Idle CPU and memory:** 0.08% of a core with the window open, 0.02% minimized, 78 MB working set, 2 MB managed. No GDI, USER, handle or heap growth over 8 game switches, 24 toasts and 5 minutes of idling. Threads settle at 9-14.
- **Everything pauses when the window is not in front:** player stats, auto-detect and the live ping (1 per 15 s). `appActive` is a cheap `GetForegroundWindow` call, asked each time, so a missed focus event cannot leave it stuck.
- **Timers:**
  - `RoundButton`/`SoftControl`/`Toggle` animation timers (15 ms) run only during a hover or slide, about 5 ticks, then stop.
  - The `StateCard` dots timer (380 ms) runs only in Loading, Testing and Locking states.
  - Toast timers (30 ms) exist only while a toast is on screen and are disposed with it.
  - The `DevPanel` 30 s timer only exists in developer mode, and only reloads while visible.
  - App-wide: the 10 s auto-detect tick, the 3 h update tick and the 60 s heartbeat. All cheap when idle.
- **Painting:** fonts are cached per control or static (the one per-paint font in `Segmented` and `GameTile` costs 2 microseconds); brushes and pens are `using`-scoped; flags and icons are cached bitmaps from `Brand`; controls are double-buffered. Only `Segmented` (settings dialog) creates a font per paint.
- **Settings I/O:** 0.07 ms per read. `SeenZones` is cached for an hour. Nothing blocks the UI thread measurably (a `Telemetry.Send` from the UI thread does about three reads, 0.2 ms).
- **Telemetry transport:** the HTTPS connection is reused between heartbeats (idle 60 s is under the 100 s default). Calls are fire-and-forget on the pool with 4-8 s timeouts. `SendNow`/`Exit` wait at most 3-4 s.
- **Gzip on `Http.Get`:** GitHub, deadlock-api, Steam and Google data arrive compressed (GitHub 29 KB to 4.7 KB, SDR 21 KB to 7.6 KB).
- **IP lists cache:** 24 h on disk, used stale when offline, so the 2.7 MB AWS file is downloaded at most once a day and only for locked AWS games.
- **Shared stats via Supabase:** `SharedDeadlock` caches for 60 s, so many copies of the app cost deadlock-api nothing. The game list `GetSDRConfig` is fetched once per game per run.
- **Ping test:** about 2 s, 23 KB of ICMP, stops early after 2 losses in a row, and skips regions blocked by the lock.
- **Startup:** window in about 0.3 s. Slow work (`SystemInfo`, firewall check, leftover cleanup, server list, update check) is on pool threads. Startup spawns 3 short processes (`pktmon filter list`, 2 `netsh`).
- **Process spawns at idle:** none, except one `netsh` every 2 min while locked (item 9). Auto-detect only runs with the game running and the window in front.
- **Memory of the fonts and images:** embedded Readex fonts are loaded once (4 ms) and the icons are cached in `Brand`.
