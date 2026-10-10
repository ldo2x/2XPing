# Changelog

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
