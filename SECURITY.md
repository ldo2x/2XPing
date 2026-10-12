# Security

## Reporting a problem

If you find a security issue in 2X Ping, please **do not** open a public issue.
Contact the developer (lDo2x) privately through GitHub instead, and give them time to fix it before sharing details.

## What 2X Ping does on your PC

2X Ping runs as administrator because some features need it. Everything it changes is listed here, and all of it is undone when you stop the feature or close the app:

| Feature | What it changes | Undone when |
|---|---|---|
| Region lock | Adds Windows Firewall rules named `2XPing-RegionLock` | You press Unlock or close the app (also cleaned on next start) |
| Relay routing | Installs a WireGuard tunnel service named `2xping` | You press Stop or close the app |
| Detect my server | Runs a 15-second `pktmon` UDP capture, then deletes it | Right after the capture |
| Raise game priority | Sets the game process priority to High | When the game closes |

2X Ping **never** reads or modifies game files or game memory.

## Usage statistics (beta)

To improve the beta, 2X Ping sends usage statistics to the developer's own database (Supabase) while it is open.
This is shown on first launch, and accepting it is required to use the beta.

| Sent | Why |
|---|---|
| The name you type on first launch, and your computer's name | Tell users apart |
| A random install ID | Count devices |
| City, country and internet provider (looked up from your internet address; the address itself is not stored) | See where the app is used, and compare ping by provider |
| Game, server and ping in use, app and Windows version, language and layout | Usage statistics |
| Once per launch: processor, graphics card, memory, screen size and scaling, Windows edition and build, system language and time zone, laptop or desktop, Wi-Fi or cable and its link speed, which of the supported games are installed, how long the app took to open | Know what PCs it runs on and why ping differs |
| Every result of a ping test (each region's ping, loss and jitter, and how long the test took) | Improve the server lists and the best-server choice |
| Events (ping test, lock, unlock, server check, boost, reset, update, settings changed, buttons used, session length) and error logs | Find and fix problems, see which features are used |
| Problems the app detects by itself (e.g. playing on a server outside your lock, a lock whose firewall rules disappeared, Windows Firewall off, a failed download, heavy packet loss, a crash), once each per session, with the details needed to fix them | Fix bugs without you having to report them |
| The game server addresses the game talks to (servers only, never other traffic) | Build server lists for games without official ones |

Nothing else is collected: no passwords, no files, no IP address, no list of your other programs, no browsing data. The app can only **write** these statistics; it cannot read anyone's data.
To have your data removed, contact the developer (Discord `2x.x`) with the name you used.

## Data sent to other services

| Service | What is sent | Why |
|---|---|---|
| Steam Web API | Game app id | Online player count, Valve server list |
| deadlock-api.com | A fixed statistics query | Deadlock players per region |
| ip-ranges.amazonaws.com, www.gstatic.com | Nothing (download only) | Amazon and Google Cloud game server IP ranges |
| stat.ripe.net | Nothing (download only) | Blizzard network IP ranges (Overwatch 2) |
| gamelift-ping.*.api.aws | A small ping packet | Dead by Daylight ping per region |
| ip-api.com | The **game server** IP you detected, and a once-a-day lookup of your own city/country | Show locations |
| Supabase (developer's database) | The usage statistics listed above | Beta statistics |
| api.github.com, github.com | Nothing (download only) | Check for a newer version and download it |
| Your own relay agent | Game server address + your token | Measure relay -> server ping |

There are no accounts or passwords; apart from the usage statistics above, nothing about you is collected.

## Updates

Updates are required during the beta. When a new version is out, 2X Ping shows it and you get 3 days to update; after that the old version only offers to update or close.
**Update now** downloads the release zip from this repository's GitHub Releases, checks it against the published SHA256 file, and refuses it if they don't match. It then replaces `2XPing.exe` and reopens it.
A release whose notes contain `#optional` is never forced.

## Relay notes

- WireGuard configs contain private keys. They are stored in `%APPDATA%\2XPing` and must never be shared or committed.
- The relay latency agent uses plain HTTP protected by a random token. Keep the token private.
