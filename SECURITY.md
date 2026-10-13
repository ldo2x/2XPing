# Security

## Reporting a problem

If you find a security issue in 2X Ping, please **do not** open a public issue.
Contact the developer (lDo2x) privately through GitHub instead, and give them time to fix it before sharing details.

## What 2X Ping does on your PC

2X Ping runs as administrator because some features need it. Everything it changes is listed here, and all of it is undone when you stop the feature or close the app. If the app is killed or crashes, the next start removes what was left:

| Feature | What it changes | Undone when |
|---|---|---|
| Region lock | Adds Windows Firewall rules named `2XPing-RegionLock` | You press Unlock or close the app (closing always removes them, even mid-change); after a crash, on next start |
| Relay routing | Installs a WireGuard tunnel service named `2xping` | You press Stop or close the app; after a crash, on next start |
| Detect my server | Runs a 15-second `pktmon` UDP capture, then deletes it | Right after the capture |
| Raise game priority | Sets the game process priority to High | When the game closes |

2X Ping **never** reads or modifies game files or game memory.
Windows tools (`netsh`, `pktmon`, `netstat`, `ipconfig`) are always started from `C:\Windows\System32`, never from the app's own folder.
Still, keep `2XPing.exe` in a folder only you (or administrators) can write to, such as `C:\Program Files\2X Ping`.

## Usage statistics (beta)

To improve the beta, 2X Ping sends usage statistics to the developer's own database (Supabase) while it is open.
This is shown on first launch, and accepting it is required to use the beta.

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
| Problems the app detects by itself (e.g. playing on a server outside your lock, a lock whose firewall rules disappeared, Windows Firewall off, a failed download, heavy packet loss, a crash), once each per session, with the details needed to fix them | Fix bugs without you having to report them |
| After a server check you start: up to 8 addresses the game talked to, once voice chat and service hosts are filtered out, and only for supported games with dedicated servers (never for custom servers, where other players' addresses could show up) | Build server lists for games without official ones |

Nothing else is collected: no passwords, no files, no list of your other programs, no browsing data. 2X Ping never sends or stores your IP address; like any website, the statistics server and the location service see it when the app connects. The app can only **write** these statistics; it cannot read anyone's data.
The developer's own test tool (developer mode, which needs the developer's private key and is never on for players) also records connection addresses and program names during a lock test on the developer's PC.
To have your data removed, contact the developer (Discord `2x.x`) with the name you used.

## Data sent to other services

| Service | What is sent | Why |
|---|---|---|
| Steam Web API | Game app id | Online player count, Valve server list |
| deadlock-api.com | A fixed statistics query | Deadlock players per region |
| ip-ranges.amazonaws.com, www.gstatic.com | Nothing (download only) | Amazon and Google Cloud game server IP ranges |
| stat.ripe.net | Nothing (download only) | Blizzard network IP ranges (Overwatch 2) |
| gamelift-ping.*.api.aws | A small ping packet | Dead by Daylight ping per region |
| ipwho.is (HTTPS) | A once-a-day lookup of your own city, country and provider (at most every 30 minutes when it fails) | Usage statistics |
| ip-api.com | The **game server** IP you detected; your own location only if ipwho.is doesn't answer | Show locations |
| Supabase (developer's database) | The usage statistics listed above | Beta statistics |
| api.github.com, github.com | Nothing (download only) | Check for a newer version and download it |
| Your own relay agent | Game server address + your token | Measure relay -> server ping |

There are no accounts or passwords; apart from the usage statistics above, nothing about you is collected.

## Updates

Updates are required during the beta. When a new version is out, 2X Ping shows it and you get 3 days to update; after that the old version only offers to update or close.
**Update now** downloads the release zip from this repository's GitHub Releases, checks it against the published SHA256 file, and refuses it if they don't match. It extracts the new `2XPing.exe` next to the old one, checks it again right before the swap, replaces `2XPing.exe` (putting the old one back if anything fails) and reopens it.
The SHA256 file comes from the same GitHub release, so it protects against a damaged download, not against someone who takes over the GitHub account. Signed releases are planned.
A release whose notes contain `#optional` is never forced.

## Relay notes

- WireGuard configs contain private keys. The relay configs you add are copied to `%APPDATA%\2XPing\relays` and must never be shared or committed. The tunnel config 2X Ping writes for WireGuard (`%APPDATA%\2XPing\2xping.conf`) is deleted when the tunnel is removed (Stop, closing the app, or the next start after a crash).
- The relay latency agent uses plain HTTP protected by a random token. Keep the token private.
