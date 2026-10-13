# 2X Ping robustness and security audit (read-only)

Branch simple-main-screen. Paths are under C:\Dev\2XPing. Line numbers are from the current tree.

## Critical

**C1. A failed startup statistics post crashes the app.**
- `src/Telemetry.cs:54-60`: `Telemetry.Start` queues a ThreadPool work item that calls `Post("start", ...)` with no try/catch. `Post` ends in `Http.Send`, which throws on offline, on a 4xx/5xx from `checkin()`, or on a paused or deleted Supabase project.
- .NET Framework terminates the process on an unhandled ThreadPool exception. There is no app.config or `legacyUnhandledExceptionPolicy`; `build.ps1` compiles with csc and no config.
- Result: with consent given, every launch while offline, or while Supabase is down, crashes about a second after the window opens. The Supabase free tier pauses idle projects, so this can hit every user at once.
- `CrashHandler.Report` then runs `Telemetry.SendNow`, which fails too, but it is wrapped in try/catch.
- Fix: wrap the work item body in try/catch (`Send` and `SendNow` already do). Add an app.config with `<legacyUnhandledExceptionPolicy enabled="1"/>`, or wrap every `QueueUserWorkItem` and Timer callback. Grep showed the other callbacks are guarded: `Telemetry.cs:132`, `Telemetry.cs:427`, and the heartbeat callback.

**C2. Update integrity depends only on the GitHub account, and the update runs as admin.**
- `Update.cs:117-125`: the SHA256 comes from the same release as the zip. It proves the download was not corrupted, not that it is authentic. There is no Authenticode check and no signature or pinned key.
- Anyone who gets a GitHub release written (stolen token or PAT, compromised Actions workflow) gets code execution as admin on every install. The 3-day forced update makes that fast.
- `Latest()` (`Update.cs:35-64`) also accepts pre-releases and any tag that matches `\d+.\d+.\d+`. A stray tag therefore forces everyone to update.
- Fix:
  - Sign the exe, or sign the sha256 file with an offline key (Ed25519 or RSA) and embed the public key in the app.
  - Verify `WinVerifyTrust` or the signer thumbprint of the extracted exe before the swap.
  - Restrict the release channel (ignore pre-releases unless opted in).
  - Protect the repo: branch and tag protection, 2FA, and an environment-gated release workflow.

## Important

**I1. Update swap can leave a corrupt or missing exe, and has a time-of-check/time-of-use hole.**
- `Update.cs:101-103`: `File.Move(fresh, exe)` goes from `%TEMP%` to the exe folder.
  - If these are different volumes, .NET copies and then deletes, so it is not atomic.
  - Disk full or a kill mid-copy leaves a partial `exe`.
  - The rollback `File.Move(old, exe)` then throws because the destination already exists, so the PC ends with a broken exe.
- There is also a gap between `File.Move(exe, old)` and the second move where no exe exists.
- The zip hash is checked (`Update.cs:122-125`), but the extracted `%TEMP%\2XPing-update\2XPing.exe` (`:129-131`) is not re-checked before it is moved into place. The path is fixed and predictable.
  - A non-elevated process of the same user can swap that file between extraction and move.
  - That is a UAC elevation-of-privilege path, because the app runs as admin.
- Fix:
  - Extract to a fresh randomly named directory (`Directory.CreateDirectory` with a GUID, user-only ACL), or extract directly to `exe + ".new"` in the exe's own directory.
  - Hash the extracted file and compare it to a hash recorded at extraction.
  - Then do the rename dance on the same volume.
  - In the catch, delete the partial `exe` before restoring `.old`.
- `Process.Start` failure at `Update.cs:105` (antivirus block) is reported as "couldn't update" even though the exe has already been replaced.

**I2. The new process can exit silently if the old one is slow to close.**
- `Update.cs:182-190`: `FinishUpdate` waits at most 20 s for the old PID.
- The old process runs `FormClosing` (`MainForm.cs:155-162`) on the UI thread. That does `Tunnel.Stop()` (up to 20 s), `ServerLock.Remove()` (two netsh calls, up to 20 s each), and `Telemetry.Exit` (up to 6 s of geo lookup plus 3 s).
- The mutex is released only at process exit. If the wait times out, `Program.cs:23-35` sees `first == false`, signals the old window, and returns with no message.
- Result: after an update the app does not come back, and the user sees nothing.
- Fix: after the wait, retry the mutex for 30-60 s. Also reduce close-time work (see I8).

**I3. A crash or kill leaves the WireGuard tunnel service installed.**
- `Tunnel.Start` runs `/installtunnelservice` (`Tunnel.cs:126-133`). The service is persistent and auto-starts at boot.
- Startup cleanup (`MainForm.cs:112-119`) removes firewall rules and the pktmon filter but never calls `Tunnel.Stop()`.
- `FormClosing` stops the tunnel only if `tunnelActive` (`MainForm.cs:158`). That flag is set only after `Start` returns (`MainForm.cs:2498`).
- A failed or partial `Start` leaves `tunnelActive` false. `StopAll` then skips `Tunnel.Stop`, so a half-installed service can be left behind.
- Closing the app during the Task.Run in `MainForm.cs:2484-2497` leaves the tunnel running too.
- SECURITY.md says the tunnel is "undone when you press Stop or close the app". That is not true after a crash.
- Fix:
  - At startup, if the `2xping` service exists and no boost is active, uninstall it. Check with `sc query WireGuardTunnel$2xping`.
  - In `FormClosing`, call `Tunnel.Stop()` unconditionally (it is a no-op if absent).
  - Set the flag before `Start`.
- Also, `2xping.conf` (`Tunnel.cs:128`) holds the relay private key in plaintext in `%APPDATA%\2XPing` and is never deleted after `installtunnelservice` copies it into WireGuard's protected store. Delete it right after install, or write it to a temp file and delete in `finally`.

**I4. Firewall lock rules can be left behind, and unlock can fail silently.**
- `FormClosing` removes rules only `if (lockSummary != null)` (`MainForm.cs:159`).
  - The lock Task (`MainForm.cs:2248-2321`) adds rules before it sets `lockSummary` on the UI thread. Closing during Apply leaves partial rules, because the pool thread is killed at exit.
  - `UnlockServers` and `ResetEverything` null `lockSummary` first and run `ServerLock.Remove()` in `Task.Run` (`MainForm.cs:2379-2384`, `:2410`). Closing right after Unlock skips the cleanup in `FormClosing`, and the background thread dies with the process.
- `ServerLock.Remove` ignores netsh exit codes (`Tunnel.cs:44-48`). `UnlockServers` logs "Lock removed, all servers are back" without checking. Only `ResetEverything` verifies with `Exists()`.
- Rules persist across reboot. If the app is never reopened after a crash, the game stays blocked (program-scoped UDP for most games, PC-wide for Valve games with `valve-all`).
- Startup cleanup (`MainForm.cs:115`) races with a lock the user applies in the first seconds. It runs in a Task and checks `Exists` before `Remove`, so it could delete a freshly applied lock. The window is small.
- Fix:
  - Always call `ServerLock.Remove()` in `FormClosing` (synchronously, with a short timeout), not conditionally.
  - Track an "in-flight" flag so close waits for or cancels Apply.
  - Make `Remove` return success and verify with `Exists` in `UnlockServers`.
  - Do startup cleanup before the UI is interactive, or gate Lock until it finishes.
  - Optionally add a dead-man option: a scheduled task or watchdog service that removes the rule group if 2XPing is not running.

**I5. Lost settings and install IDs from unsynchronised file access.**
- `Storage.ReadSettings/SetSetting` (`GameDb.cs:506-532`) are called from the UI thread and from ThreadPool threads: `EnsureLocation` writes 5 settings in a row, and `InstallId` writes on first read.
- `File.WriteAllLines` truncates the file. A concurrent `ReadAllLines` gets an IOException, which is swallowed (`ReadSettings` returns an empty dictionary). The next `SetSetting` then rewrites the file with only one key, so consent, nickname, install_id, update_seen and others are lost.
- A power loss mid-write has the same effect.
- `InstallId` (`Telemetry.cs:32-39`) has a read-then-write race, and if the write fails it generates a new GUID on every call. Every event then arrives as a different "install". This inflates the installs table and bypasses the per-install flood guard.
- The same unlocked, non-atomic pattern is in `SavePicks`, `AddDetected`, `SaveRelays`, `SaveLast`, and `File.AppendAllLines` for `custom.txt`. `SavePicks`, `AddDetected` and `SaveRelays` also have no try/catch, so an IOException surfaces as an app error.
- Fix: one static lock around all settings I/O. Cache settings in memory. Write to a temp file and `File.Replace`. Cache the install id in a static after first read. Do not overwrite on a read failure.

**I6. Unobserved exceptions in `Task.Run` leave the UI stuck.**
- Many bodies have no try/catch. The clearest is `RunTest` (`MainForm.cs:1525-1560`), where `Parallel.ForEach` and `ProbeFromRelay` run unguarded and `busy`, `btnTest.Enabled` and `testGame` are reset only on the success path. Others: `:1077`, `:2046`, `:2093`, `:2379`, `:2686`.
- A throw gives an AggregateException that nobody observes. `busy` stays true forever, the test button stays disabled, and `ResetEverything` answers "wait for the current test".
- Fix: wrap the body in try/catch/finally that always resets state on the UI thread and logs the error.
- `Ui()` (`MainForm.cs:968-973`): if the handle is not yet created, `InvokeRequired` is false and the action runs inline on the calling worker thread (cross-thread control access). `Ui` runs from startup tasks before `Show`, for example via the dev-mode `Issues.Reported` hook. It catches only `InvalidOperationException`. Check `IsHandleCreated`, and catch `ObjectDisposedException` too.

**I7. Telemetry sends more than SECURITY.md declares.**
- *Crash and error stack traces and messages:*
  - `Brand.cs:307-309` sends `ex.ToString()` as `stack` and `ex.Message` as detail.
  - `MainForm.cs:2506` sends `"boost via ...: " + ex` (the whole exception).
  - Also sent: `Update.cs:261-262` (err), `IpLists.cs:35`, `MainForm.cs:2318`, `:1096`, `:1868`, `:2069` (`ex.Message`).
  - IOException, FileNotFound and WireGuard output messages contain full paths such as `C:\Users\<realname>\AppData\Roaming\2XPing\...` or `%TEMP%`. That leaks the Windows username, which SECURITY.md says is not collected.
  - Fix: scrub `Environment.UserName`, the user profile path, and `%TEMP%` (replace with `%USERPROFILE%`, and so on). Send only the exception type and the top few frames.
- *`traffic` events:*
  - `MainForm.cs:1854-1858` sends up to 8 `ip:port xN` entries before `ServerHits` filtering.
  - For custom or "any game" capture, `Detector.Capture` (`Stats.cs:225-290`) takes any public UDP peer except a noisy-app list.
  - That can include peer-to-peer or other players' home IPs, and calls or apps not on the list. SECURITY.md says "servers only, never other traffic".
  - Fix: send only addresses that survive `ServerHits` and only for games with known dedicated servers, or limit this to a developer opt-in.
- *`locktest` events:* `LockTest.Data` (`LockTest.cs:612-641`) includes remote IPs from the whole PC (TCP SYN destinations, so browsing hosts) plus process names (30 chars). It is reachable only in dev mode (needs `dev.key`), so it is low risk. Add a note, and never expose it to normal users.
- *`info` fields not listed in SECURITY.md:* the `.NET` release, `wireguard` installed, `autotest`, `hidden_games`, `cores`, `locale` and `ui_lang`. These are minor; list them.
- *Consent:* it is required to use the app, and there is no opt-out in Settings. This is stated in the notice, but consider a toggle that stops sending while still allowing the app to run.
- *Geo lookup:* it uses plain HTTP (`Telemetry.cs:98`, `http://ip-api.com`), so a network attacker can alter the city, ISP and country that are then posted to the database. Use HTTPS (ip-api's free tier is HTTP-only), or use another source.
- *Supabase gateway logs:* the user's IP is seen by Supabase's gateway and logs even though the app does not store it. SECURITY.md could say "not stored by 2X Ping".

**I8. Shutdown and crash handling can block for a long time.**
- `EnsureLocation` (`Telemetry.cs:84-110`) runs on every `Post`. When the lookup fails (offline, or rate-limited by ip-api at 45 requests per minute), nothing is cached, so every heartbeat, event and `Exit` waits up to 6 s again and hammers ip-api. It also reads `settings.txt` from disk on every call.
- `Telemetry.Exit()` and `SendNow` run synchronously on the UI thread in `FormClosing`: `EnsureLocation` 6 s + post 3 s, plus `Tunnel.Stop` and `ServerLock.Remove`. Offline, the window can hang for 10 to 40 s while closing.
- `netsh`, `wireguard`, `pktmon` and `netstat` helpers (`Tunnel.cs:64-76`, `:142-154`, `Stats.cs:321-331`) call `ReadToEnd()` before `WaitForExit(20000)`, so the timeout never fires if the child hangs. A hung `netsh` blocks the lock thread, and close blocks forever. Reading stdout then stderr sequentially can also deadlock on large stderr.
- Fix: read both streams asynchronously, call `WaitForExit(timeout)` and kill on timeout, add a negative cache for geo lookups (retry after 10-60 min), and do shutdown cleanup on a background thread with a bounded wait.

**I9. Supabase side.** The client-side details are in `tools/supabase-setup.sql`.
- *Flood guard is per install_id only (`:98-100`):*
  - The publishable key is in the repo and the exe, so anyone can call `checkin()` with fresh random UUIDs.
  - Each call creates an `installs` row and up to 60 events a minute each, with up to 32 KB of `data` and 4000 chars of `detail`.
  - This can fill the 500 MB free tier, trigger quota or pause, and break telemetry for everyone (see C1).
  - There is also no retention policy: no purge of `events` or `installs`, only `game_stats_history` at 180 days.
  - Fix: put an Edge Function or Cloudflare Worker in front with per-IP limits, and add a global rate or row-count guard inside `checkin`. Add a daily cron deleting events older than about 90 days and installs not seen for about 1 year. Support deletion by nickname/install on request, as SECURITY.md promises.
- *Spoofing and pollution:*
  - Any client can post any `kind` (up to 20 chars), nickname, version, ping, or `issue`/`crash` events, and can mark itself "online".
  - The dev panel reads these as truth (`DevPanel.Fill`).
  - Treat them as untrusted. `kind` should be validated against an allow-list.
  - `installs` can also be overwritten by anyone who knows another install's UUID. UUIDs are not exposed publicly, so the risk is low.
- *Function hardening:*
  - `security definer ... set search_path = public` should also pin `pg_temp` last, for example `set search_path = public, pg_temp`.
  - A bad `install_id` or non-integer `ping` raises a cast error (HTTP 400), which currently crashes the client (C1).
  - Oversized `info` or `data` is silently dropped to NULL, with no signal.
  - No limit applies to total request body size, so PostgREST's own limit is the only cap.
- *`game_stats` public read:*
  - `using (true)` for anon is acceptable. The table holds only Deadlock region counts and has no PII. Select is granted; insert and update are not.
  - `game_stats_history`, `stats_requests`, `events` and `installs` are RLS-enabled with all grants revoked, which is correct.
  - `refresh_stats()` is revoked from anon, correct.
- *`refresh_stats()`:* a non-JSON or HTML error body in `resp.content::jsonb` aborts the whole function (steps 1-2), so the re-ask in step 2 never runs. The job then fails every minute until someone intervenes. Wrap each parse in `begin ... exception when others then null`.

**I10. Process spawning by bare name from an admin process.**
- The following start `netsh`, `pktmon`, `netstat` and `ipconfig` by bare name, with `UseShellExecute=false`: `Tunnel.cs:66`, `Stats.cs:321`, `MainForm.cs:2703`, `:2941`.
- CreateProcess searches the application directory first, then the current directory, then System32. If the exe sits in a user-writable folder such as Downloads and a planted `netsh.exe` exists next to it, it runs elevated.
- The exe itself in a user folder is the same class of issue, but the planting only needs write access to one folder.
- Fix: use `Environment.SystemDirectory` for netsh, pktmon, netstat, ipconfig and sc, and `Tunnel.WgExe` is already full-path.
- The SECURITY.md claim that the app is only "run as administrator because some features need it" is true. Consider a first-run note advising installation in `%ProgramFiles%` or another admin-only folder.

## Minor

- **M1. Lock input validation.**
  - `ServerLock.Cidr` (`Tunnel.cs:15-16`) uses `$`, which in .NET matches before a trailing `\n`. Use `\z`. It accepts octets above 255 and prefixes above 32, so one bad entry in an external list (AWS, GCP, RIPE) makes netsh fail and the entire lock fail. Validate ranges.
  - Quoting of `program="..."` is fine: Windows paths cannot contain `"`, `FindExe` returns an existing file, and remote addresses are matched by digits only, so no argument injection was found.
  - A lock over very large `LockSpace` lists (gcp:* or large aws:) issues many netsh calls (100 addresses per rule, out and in) with no progress or cancel, and a kill mid-way leaves a partial rule set that is only cleaned at next start.
- **M2. Cache poisoning of IP lists.** `IpLists.Cached` (`IpLists.cs:20-38`) returns the cached file if it is under 24 hours old without validating it, and writes with non-atomic `File.WriteAllText`. A truncated or HTML (captive portal) file makes `Json()` throw for a day. The stale fallback applies only when the download fails. Validate before saving, write atomically, and fall back if parse fails.
- **M3. pktmon is global state.** `Detector.Capture` and `LockTest.Record` run `pktmon filter remove` unconditionally (`Stats.cs:247`, `LockTest.cs:662`). This wipes any filters the user had configured, and `Cleanup` runs `pktmon stop` on someone else's capture. Only startup cleanup is guarded by the "2XPing" name check. A killed capture can leave up to 512 MB of `.etl` in `%APPDATA%\2XPing\capture` until the next run.
- **M4. Single instance.**
  - The mutex is `Local\`, per session. Under fast user switching, two sessions each run an instance, and both share the one machine-wide firewall rule name, so one user's Unlock or startup cleanup deletes the other's lock. Use `Global\` or add a per-session rule name.
  - A second launch while the first has a hung UI gives no feedback.
  - A second launch in the window between mutex creation and event creation exits silently (`Program.cs:19-36`).
- **M5. Dev key (`dev.key`).**
  - It is never embedded, never sent anywhere except `SupabaseUrl` (https constant) via `Headers()`, and never logged. grep shows only `Telemetry.cs:159-170`, `:326` and `MainForm.cs:2719-2729`.
  - It is plaintext in `%APPDATA%\2XPing`. Protect it with DPAPI (`ProtectedData`, `CurrentUser`), or keep it in Windows Credential Manager.
  - The `set` accessor can throw (`File.WriteAllText`, unguarded) when the folder is read-only. `Headers(null)` throws NullReferenceException if no key (caught upstream).
  - The secret key is shown in a plain text box in `InputDialog`.
  - Dev mode is just a setting plus the file, so a normal user cannot do anything without a valid key. Redirects: `HttpWebRequest` follows redirects with custom headers, but the URL is fixed to the Supabase host, so exposure is theoretical.
- **M6. Swallowed exceptions that hide real failures.**
  - `ServerLock.Remove` ignores exit codes. `Storage.SetSetting` swallows write failures, so consent and update_seen silently fail to persist and the user sees the Welcome dialog every launch.
  - `Updater.Latest()` returns null on any error, so no feedback when GitHub is unreachable or rate-limited (60 per hour per IP), and a forced update never triggers.
  - `SystemInfo.InstalledGames` throws on one bad Epic manifest and loses the entire `games_installed` value.
  - `Brand.Open` swallows everything.
  - `MakeDetected` (`GameDb.cs:655-668`) throws ArgumentOutOfRange on a corrupt `detected.txt` line, which makes that game's server list fail to load every time.
- **M7. Thread-safety of shared state.**
  - `Region.Direct`, `ViaRelay`, `BestEndpoint` and `Relay.Ping` are written from `Parallel.ForEach` threads (`MainForm.cs:1531-1554`) while the UI reads and renders them. These are reference writes, so no tearing, but the UI can see half-updated rows.
  - `g.Regions` is replaced wholesale by `GameDb.Load` on a worker (`GameDb.cs:421`) while the UI enumerates it.
  - `Detector`, `Storage` and `Telemetry.Ping/Game/Server` have no locks (the last are volatile or int). `Issues.sent` and `recent` are correctly locked. `Pinger.dns` is correctly locked.
- **M8. Update dialog and install details.**
  - `%TEMP%\2XPing-update\update.zip` and leftover files are not removed after success; only the next `Fetch` deletes them.
  - No size cap on the download or the SHA text.
  - `Download` does not compare received bytes against Content-Length, though the hash check catches truncation.
  - A partial download throws on timeout (`ReadWriteTimeout` 20 s), is reported as "couldn't update", and does no harm because the zip is verified before the swap.
  - A forced-update screen with a broken release (missing zip or sha asset) locks everyone out after 3 days with only "Download page" or "Close".
  - `FirstSeen` is stored in the editable settings file, so the grace period is user-editable and resets if the file is lost.
- **M9. Misc.**
  - `FlushDns` (`MainForm.cs:2703`) does not catch a `Process.Start` failure.
  - The listener thread in `Program.cs:50-63` catches only ObjectDisposedException.
  - `Telemetry.Start` skips the heartbeat timer when consent is absent, which is intended.
  - Test and tooling code (`tests/`, `tools/MakeLogo.cs`) was not audited for security.

## SECURITY.md accuracy summary

| Claim | Status |
|---|---|
| Region lock cleaned "on next start" | True for firewall rules. Not verified on every close path (I4). |
| Relay tunnel undone on Stop or close | False after a crash or kill (I3). |
| "No IP address" stored | App sends none. Supabase and ip-api see it by nature. |
| Game server addresses "servers only, never other traffic" | Not strictly true for custom or P2P games (I7). |
| "No list of your other programs" | True for normal users. Lock-test data has process names but is dev-mode only. |
| Names, computer name, ISP, city, country, hardware | Matches code (`Telemetry.cs:145`, `SystemInfo.cs`). |
| Local paths and username | Not declared. Leaked through exception text (I7). |
| SHA256 check on updates | Implemented, but integrity only, not authenticity (C2). |
| `dev.key` never sent or logged | Confirmed by grep. |

## Suggested order of work
1. C1 (try/catch around the startup post, plus a safety net). 2. I5 (settings lock and atomic writes). 3. I3 and I4 (tunnel and firewall cleanup on startup and on close). 4. I1, I2, C2 (updater). 5. I7 (scrub paths, narrow `traffic`). 6. I8 and I10 (process helper with full paths and real timeouts). 7. I9 (Supabase rate limit and retention). 8. The Minor list.
