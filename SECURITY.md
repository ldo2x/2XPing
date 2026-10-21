# Security

## Reporting a problem

If you find a security issue in 2X Ping, please **do not** open a public issue.
Contact the developer (lDo2x) privately through GitHub instead, and give them time to fix it before sharing details.

## What 2X Ping does on your PC

2X Ping runs as administrator because some features need it. Everything it changes is listed here, and all of it is undone when you stop the feature or close the app. If the app is killed or crashes, the next start removes what was left:

| Feature | What it changes | Undone when |
|---|---|---|
| Region lock | Adds Windows Firewall rules named `2XPing-RegionLock` (the other regions' game server addresses; in Deadlock also Valve's relay entry points, like Dubai, when they would lead into a blocked region). While a lock is on, 2X Ping checks every 2 minutes that the rules are still there, never while a game is in front | You press Unlock or close the app (closing always removes them, even mid-change); after a crash, on next start |
| Relay routing | Installs a WireGuard tunnel service named `2xping` | You press Stop or close the app; after a crash, on next start |
| Which server am I on? | Runs a `pktmon` UDP capture filtered to the game's own ports: 15 seconds when you press the button, 8 seconds for the automatic check (every 30-90 seconds, only while the game runs and 2X Ping's window is in front) | Right after the capture |
| Raise game priority | Sets the game process priority to High | When the game closes |

2X Ping **never** modifies game files and never reads or modifies game memory. The only game file it reads is Deadlock's console log (`game\citadel\console.log`), and only if you turned it on with the `-condebug` launch option: it looks for the lines that name the match's datacenter and the relays the game connects through (with their delays), every 20 seconds while Deadlock runs. Nothing from that file is sent anywhere except datacenter and relay short names and those delays in the usage statistics (the match report below); never addresses, account or player ids.
Windows tools (`netsh`, `pktmon`, `netstat`, `ipconfig`) are always started from `C:\Windows\System32`, never from the app's own folder.
Still, keep `2XPing.exe` in a folder only you (or administrators) can write to, such as `C:\Program Files\2X Ping`.

## Usage statistics (beta)

To improve the beta, 2X Ping sends usage statistics to the developer's own database (a Supabase project that belongs to the developer, lDo2x) while it is open.
This is shown on first launch, and accepting it is required to use the beta.

**Share beta statistics** («شارك إحصائيات البيتا») in **Settings > Options** decides how much is sent. It is **on** by default, and you can turn it off at any time; the change counts from the next thing the app sends, no restart needed.

- **On:** everything in the table below.
- **Off:** only anonymous usage. Your name is replaced by `(anonymous)`; your computer's name, city, country, internet provider and network (AS) are sent empty, which also clears what the database kept from before; the launch details shrink to the Windows version, how long the app took to open and whether it was just updated, plus a mark that the install is anonymous. 2X Ping makes **no** location lookup of your own internet address while it is off. Still sent: the random install ID, the app and Windows version, the app's language and layout, the game, server and ping in use, ping-test results, the regions you pick and your lock, the server detected, match reports, problems the app detects, error reports, and which features you use (the rows from "Game, server and ping" down, except the launch details).

### With the switch on

| Sent | Why |
|---|---|
| The name you type on first launch, and your computer's name | Tell users apart |
| A random install ID | Count devices |
| City, country and internet provider (looked up from your internet address; the address itself is not stored) | See where the app is used, and compare ping by provider |
| Game, server and ping in use, app and Windows version, language and layout | Usage statistics |
| Once per launch: processor and its core count, graphics card, memory, screen size, number of screens and scaling, Windows edition and build, .NET version, system language, locale and time zone, the app's language, laptop or desktop, Wi-Fi or cable and its link speed, which of the supported games are installed and which you hid, whether WireGuard is installed, the automatic ping test setting, whether it was just updated, how long the app took to open | Know what PCs it runs on and why ping differs |
| Every result of a ping test (each region's ping, loss and jitter, and how long the test took) | Improve the server lists and the best-server choice |
| Events (ping test, lock, unlock, server check, boost, reset, update, settings changed, buttons used, session length) and error logs | Find and fix problems, see which features are used |
| Error reports: the error's type and message and the first lines of where in the app it happened. Your Windows user name and folder paths (e.g. `C:\Users\<you>`) are replaced before anything is sent | Fix crashes |
| A match report, once per match about a minute after it starts, whether a lock is on or not: the game, the datacenters the lock picked and their regions, the relays it closed or kept open, the Dubai setting, the ping the card expected, the datacenter and region the match was on, how that was found (the game's console log or a server check), the relay the game entered by, the game's own ping, the relays' delays to the server, whether the match was inside the lock, how long the queue took and how old the lock was. Only datacenter short names (like `fra`), never addresses | Check the lock does what it promises and find where it fails, without you having to report anything |
| Problems the app detects by itself (e.g. playing on a server outside your lock, a lock whose firewall rules disappeared, Windows Firewall off, a failed download, heavy packet loss, a crash), once each per session, with the details needed to fix them | Fix bugs without you having to report them |
| After a server check you start: up to 8 addresses the game talked to, once voice chat and service hosts are filtered out, and only for supported games with dedicated servers (never for custom servers, where other players' addresses could show up) | Build server lists for games without official ones |

Nothing else is collected: no passwords, no files, no list of your other programs, no browsing data. 2X Ping never sends or stores your IP address; like any website, the statistics server and the location service see it when the app connects. The app can only **write** these statistics; it cannot read anyone's data. The only thing it reads from that database is the shared Deadlock player numbers, which are public and the same for everyone.
The developer's own test tool (developer mode, which needs the developer's private key and is never on for players) also records connection addresses and program names during a lock test on the developer's PC.
To have your data removed, contact the developer (Discord `2x.x`) with the name you used. To stop sending anything at all, close 2X Ping (nothing is sent while it is closed) or remove it (see "Uninstall" in the [README](README.en.md#uninstall)).

## Data sent to other services

| Service | What is sent | Why |
|---|---|---|
| Steam Web API | Game app id | Online player count, Valve server list |
| deadlock-api.com | Nothing (download only: `/v1/info` and `/v1/matches/active`), and only when the shared numbers from the developer's database (refreshed there every 2 minutes) are missing or more than 10 minutes old: then about 200 KB every 5 minutes, only while the window is in front | Deadlock players per region |
| ip-ranges.amazonaws.com, www.gstatic.com | Nothing (download only) | Amazon and Google Cloud game server IP ranges |
| stat.ripe.net | Nothing (download only) | Blizzard network IP ranges (Overwatch 2) |
| gamelift-ping.*.api.aws | A small ping packet | Dead by Daylight ping per region |
| global.gcping.com and Google Cloud's per-region ping addresses | A short web request | Overwatch 2 ping per Google Cloud region |
| The game servers themselves (Valve, Tencent Cloud) | A ping (ICMP or a TCP connect) | Ping per region |
| ipwho.is (HTTPS) | A once-a-day lookup of your own city, country and provider (at most every 30 minutes when it fails); never while "Share beta statistics" is off | Usage statistics |
| ip-api.com (plain HTTP) | The **game server** IP you detected (each address at most once an hour); your own location only if ipwho.is doesn't answer (and never while "Share beta statistics" is off) | Show locations |
| Supabase (developer's database) | The usage statistics listed above, or only the anonymous part while "Share beta statistics" is off (a heartbeat every 2 minutes while the app is open, plus events); it also downloads the shared Deadlock player numbers when a game's list loads, when you come back to the window, and every 5 minutes while the window is in front | Beta statistics, player numbers |
| api.github.com, github.com | Nothing (download only); checked every 3 hours | Check for a newer version and download it |
| Your own relay agent | Game server address + your token | Measure relay -> server ping |

There are no accounts or passwords; apart from the usage statistics above, nothing about you is collected.

## Updates

Updates are required during the beta. When a new version is out, 2X Ping shows it and you get 3 days to update; after that the old version only offers to update or close.
**Update now** downloads the release zip from this repository's GitHub Releases, checks it against the published SHA256 file, and refuses it if they don't match. It extracts the new `2XPing.exe` next to the old one, checks it again right before the swap, replaces `2XPing.exe` (putting the old one back if anything fails) and reopens it.
The SHA256 file comes from the same GitHub release, so it protects against a damaged download, not against someone who takes over the GitHub account. Releases are being set up for code signing through SignPath Foundation: see the [Code signing policy](https://ldo2x.github.io/2XPing/code-signing.html).
A release whose notes contain `#optional` is never forced.

## Relay notes

- WireGuard configs contain private keys. The relay configs you add are copied to `%APPDATA%\2XPing\relays` and must never be shared or committed. The tunnel config 2X Ping writes for WireGuard (`%APPDATA%\2XPing\2xping.conf`) is deleted when the tunnel is removed (Stop, closing the app, or the next start after a crash).
- The relay latency agent uses plain HTTP protected by a random token. Keep the token private.
