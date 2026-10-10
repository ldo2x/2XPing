# Changelog

## v0.10.0-beta

- **One-click updates:** "Update now" downloads the new version, checks its SHA256, replaces the app and reopens it
- **Updates are required:** when a new version is out you get 3 days (a bar under the header counts down); after that the old version only offers to update or close. It never forces it in the middle of a match. Releases marked `#optional` are never forced
- The "check for updates" option is gone (it's always on)
- **No more popups:** notices appear as chat lines at the bottom of the window (like a game chat) and fade out by themselves; hover to keep one, click to dismiss. No OK buttons. While the app is next to the clock, important ones show as a tray balloon
- The new-version notice is a chat line plus the bar at the top; the update window only opens when you press Update now (or when the 3 days are over)
- "Which server am I on?" and "Back to normal" start right away, without a confirmation box
- **Opening 2X Ping again** brings the open window to the front (even from next to the clock) instead of an "already running" message
- **New font:** Readex Pro, embedded in the app (SIL Open Font License)
- **Lives next to the clock:** minimize it and it goes to the system tray, the lock keeps working, and the tooltip shows your live ping
- **Opens on your game:** if a supported game is running it's selected; otherwise the game you used last
- **Opens with your last results** straight away; the new test refreshes them
- Friendlier texts everywhere; the welcome screen just asks "What's your name?"
- Beta statistics now include hardware and setup details, internet provider, every ping-test result, feature use and session length (see SECURITY.md). Run `tools/supabase-setup.sql` again to store them
- Developer dashboard: **Versions** tab (who has updated) and **Devices** tab, plus more breakdowns (GPU, CPU, RAM, connection, provider, installed games, average session)
- The beta notice shows once, not after every update
- If Steam is slow, the server list is retried once instead of showing an error
- Fixed: list headers stay dark with the new font

## v0.9.3-beta (2026-10-10)

- **Much faster ping test:** every server is tested at once, and a server that doesn't answer is skipped after two tries (Deadlock: 13 s → 2 s, CS2: 17 s → 3 s)
- **Welcome screen:** asks for your name and the games you play, and tells you about the beta statistics; accepting is required to use the beta
- **Beta statistics** (see SECURITY.md): name, computer name, city/country, game, server, ping, version and error logs, sent to the developer's database; the app can only write, never read
- Your name can be changed in Settings
- Developer dashboard for the maintainer (online users, statistics, event log)
- **Overwatch 2** added: Google Cloud regions, including Dammam and Doha
- **Game icons** next to every game, and **country flags** next to every server
- **Choose your games** in Settings: only the games you tick appear in the list
- Settings window in two columns, so it fits smaller screens
- **Simpler Simple layout:** no BOOST/Stop buttons (BOOST only matters with a relay, so it stays in Advanced); "Test ping" sits next to "Play on it"
- The status bar shows the live ping of the server you locked: "Status: locked on Frankfurt · 78 ms"
- Settings open any time (no more "stop the boost first"); changes apply when a running test ends
- Settings: one calm "Back to normal" button instead of "Reset settings" + a red "Put my PC back"
- About: links in Arabic reading order
- Removed "Custom servers" to keep the app simple
- Fixed: switching games during the automatic ping test no longer leaves the new game untested
- Window titles keep "2X" in Latin digits on Arabic Windows

## v0.9.2-beta (2026-10-10)

- **New Celeste theme:** night purple, unicorn pink, lavender and sparkle gold, with a matching logo
- **"Your best server" card:** the lowest-ping server that also has enough players, with a one-click **Play on it** that locks you to it
- **Servers grouped by region** (Deadlock): each region shows its players and wait time, and the busiest one gets a "✦ most players" badge
- **Even simpler Simple layout:** just server and ping; the live graph only appears while BOOST is on

## v0.9.1-beta (2026-10-10)

- **Arabic first:** all Arabic text rewritten naturally (not translated), with a light Hijazi touch
- **Simple and Advanced layouts:** Simple shows only server, ping, players and wait time; Advanced keeps every tool
- **Settings window:** language, layout, auto ping test, update check, reset
- **"Put my PC back as it was"** (Settings, and Tools in Advanced): removes the server lock and relay route, resets game priority, clears your ticks, then verifies nothing is left
- **"Which server am I on?"** (was "Detect my server"): in Valve games it names the server you are on and confirms it is one you locked
- Fixed: server detection no longer picks up Discord or browser traffic, and refuses to guess when the game is closed
- Locked-out regions now show "blocked" instead of a timeout, and are skipped in the ping test
- Fixed cut-off text in the ping and loss columns

## v0.9.0-beta (2026-10-10)

First public **beta**.

- Ping, jitter and packet-loss tests to the real server regions of popular games
  (Deadlock, Counter-Strike 2, Dota 2, Arena Breakout: Infinite, Valorant, Fortnite, PUBG, Apex Legends, Rocket League, Dead by Daylight)
- Region lock for Valve games (Deadlock / CS2 / Dota 2): play only on the servers you pick
- Estimated players per region and estimated time to find a match (Deadlock)
- Online player count from Steam
- "Detect my server": finds the game server you are playing on (any game)
- Relay routing through your own WireGuard VPS: only game traffic goes through it
- Live ping graph, game priority boost, DNS flush
- Arabic (right-to-left) and English interface
- Update check against GitHub Releases
