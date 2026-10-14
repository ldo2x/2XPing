# Changelog

## Next (in testing)

- **A simpler main screen.** One card at the top says in plain words what is happening: locked or not, in a match, on your region or not. Under it, 2-3 region choices with ping, wait time and players, and you can pick several. Overwatch and Dead by Daylight are chosen by region; «اختيار يدوي» opens the full server list
- The advanced layout keeps the same card on top with every tool under it
- In the simple layout, «افحص البنق» under the card's button tests your ping again, and while a lock is being applied the card names it («نجهّز لك أوروبا...» / "Setting up Europe...")
- Far fewer pop-up notices in the simple layout: what the card already says (locked, "you're playing on...", unlocked) isn't repeated. Warnings, errors and update notices stay
- **Deadlock: lower ping with a lock.** The lock keeps Valve's relay-only entry points (like Dubai) open when they only lead to the regions you picked: the game enters Valve's network through the closest one and rides it to the match server. Blocking Dubai pushed players in the Gulf onto their internet provider's longer route (about +30 ms to Frankfurt for a player in Riyadh on STC)
- **Deadlock: a strict lock.** An entry point that would also lead the game into a region you didn't pick is closed, so the lock holds. The card says what that costs, with the numbers, before you press the button. The region tiles show the ping through the best open way ("via Dubai"). Valve's IPv6 relays are blocked too
- **Deadlock: Dubai hosts SE Asia matches.** Two SE Asia-locked matches from Riyadh were played in Dubai (the game's console log showed it), so Dubai is now one of SE Asia's datacenters: an SE Asia lock keeps it, and the console log names a match there «جنوب شرق آسيا (دبي)». A Europe lock still keeps Dubai open as the shortest way in for the Gulf (about 58 ms to Europe from Riyadh instead of 68), so rarely a match may land in Dubai: the card says so and points to the new setting **«دبي كطريق لأوروبا» / "Dubai as a way to Europe"** (Settings, on by default). Turn it off and Dubai is blocked unless you pick SE Asia: never a match there, but a few ms more to Europe, and the card tells you how many before you lock
- Deadlock: before locking, 2X Ping checks your shortest way into Valve's network (from your own pings and Valve's published pings between its datacenters). If the lock has to block it because matches are played there too, you're told how many ms it can add and to tick it as well
- Deadlock: relay-only locations are no longer suggested as "Your best server" (no matches are played there) and can't be ticked; the list marks them "relay only"
- **Honest "where am I playing".** A server only counts as the match when it carries at least half of the game's traffic. When the game goes through a Valve relay, which hides the match server, the card says "Entering Valve's network at Dubai; the match server can't be seen from here", with no ✓ and no false "outside your lock"
- Deadlock with the `-condebug` launch option: 2X Ping reads the game's console log (every 20 s while Deadlock runs, the file only, no network) and names the match's real datacenter, e.g. «تلعب دحين في أوروبا (فرانكفورت) ✓». A small link on the card shows how to turn it on
- Deadlock's server table is corrected: Johannesburg hosts European matches; Mumbai, Chennai and Guam are entry points only; Helsinki, Falkenstein and the other datacenters Valve lists without a relay show as "can't be locked". A datacenter Valve adds later is reported to us
- **Russia** is explained under the Deadlock regions: it's its own queue played on the European servers, so a lock can't choose it (with its player number)
- **Deadlock player numbers are now realistic** (about 49,000 online instead of about 2,000): the total comes from every match deadlock-api collects in a day (about 139,000), with the real average match length (32 min) and the players in the menus, the queue or away counted too (a match holds about 3 in 4 of them). The live-match list only holds the top 200 matches of the Watch tab, so it now only gives each region's share
- Deadlock player numbers follow the time of day: the day's total is scaled by how busy the latest complete hour was (Deadlock starts about 1,600 matches an hour at its quietest and 4,800 at its peak), instead of staying on the day's average
- The app now asks Windows whether its window is in front, instead of relying on focus events that can be missed, so it never stays paused while you're looking at it
- Player numbers can come from our database (Supabase), which fetches each region's numbers every 2 minutes for every copy of the app, and they refresh by themselves every 5 minutes while the window is in front. Without fresh shared numbers (missing, or more than 10 minutes old), each copy asks deadlock-api itself: about 200 KB every 5 minutes, only while the window is in front. A copy is kept every 10 minutes, to learn each region's busy hours
- While the game is in front, 2X Ping stays quiet: the automatic server check pauses and the live ping only refreshes every 15 s for the window title. Both refresh as soon as you come back to the window
- **Overwatch 2: a lock only works inside your game's region** (Americas, Europe or Asia, as set in the game). Locking only regions outside it finds no match. 2X Ping learns your game's region from the servers your matches are on, warns before you lock outside it, and after 3 minutes locked without a match says the region may be the reason
- Overwatch 2: two Blizzard service addresses (login and services) are no longer counted as match servers, so a lock never blocks them, and voice chat is never shown as the server you're on
- Overwatch 2: the game's menu and Practice Range traffic (Blizzard's Irvine addresses) is no longer reported or shown as the server you're on
- Overwatch 2: the regions your matches were on are remembered for 14 days, so after you move your game to another region the old one stops counting
- **Minimizing keeps 2X Ping in the taskbar** (no more tray icon): hover its taskbar button to see the live status (for example "locked on Dammam · 78 ms"). Warnings flash the button and appear when you come back to the window. Closing 2X Ping still removes the lock
- **Overwatch 2 and Dead by Daylight are marked "in development"**: the tag sits under the game's full name (names are no longer cut off), and before you lock the card says their lock is still in development
- Clearer words: queue waits read correctly in Arabic («1 إلى 3 دقايق» instead of a reversed "3-1"); one word for waiting («أقصر انتظار»); «وش» instead of «إيش»; no more «ترافيك» on player screens; BOOST is now **Speed up** («شغّل التسريع»); notices name the button you actually see
- The welcome window says what the beta statistics include and that you get 3 days to update, and tells you why "Agree and start" is greyed out
- **Lighter on your PC and internet:** no automatic ping test while the game is open (the card shows last time's pings and says how to refresh them); the heartbeat goes every 2 minutes instead of every minute and keeps one connection, so an idle 2X Ping uses about 56 KB an hour instead of 112; the update check asks GitHub "anything new?" and gets an empty answer when nothing changed; a server's location is looked up once an hour; Dead by Daylight's server lists load in milliseconds instead of more than a second; the server check no longer starts netstat and only watches the game's own ports; the firewall-rule check waits while a game is in front
- **Safer:** closing 2X Ping always removes the lock and the relay tunnel, even in the middle of a change; a tunnel left by a crash is removed at the next start; settings are written safely, so two changes at once never lose one; starting without internet no longer crashes; a stuck button can't stay disabled after an error; Windows tools always start from System32; error reports have your user name and folders removed
- **Safer updates:** the new version is unpacked next to the old one, checked again just before the swap, and the old one is put back if anything fails
- The Readex Pro SemiBold font now loads on every launch (it sometimes fell back to Segoe UI)
- Behind the scenes: the main window's logic is split into small, tested parts (no change in behaviour)

## v0.11.0-beta (2026-10-12)

- **Only fully tested games in the public version:** Deadlock, Arena Breakout: Infinite, Overwatch 2 and Dead by Daylight. The others stay hidden until they're verified; developer mode shows them with a "Trial" tag
- **New controls that match the design:** Settings and the welcome window are now made of cards, games are tiles you switch on and off, language and layout are segmented buttons, options are on/off switches, and the name is typed in a rounded field that lights up. The server list's square tick boxes are now the same round pink check
- **Pick your servers in Overwatch 2:** the 15 datacenters (Dammam, Doha, Bahrain, Frankfurt, Amsterdam…) with their real ping. Locking blocks the game's match traffic to every Overwatch server except the ones you picked, new servers included: all of Google Cloud, Amazon Bahrain and Blizzard's network come from their official lists and update by themselves
- **Pick your servers in Dead by Daylight:** its 15 Amazon GameLift regions, with the ping measured on the same beacons the game uses
- **Pick your servers in Valorant, Fortnite and PUBG:** their Amazon regions, from Amazon's official list (PUBG also uses Azure, which isn't covered)
- These locks only block the datacenters you didn't pick, and only the game's own UDP (its match traffic): login, voice chat, the Practice Range and the rest of the PC are untouched (blocking the whole network of Google and Blizzard broke the Overwatch Practice Range, so it isn't done)
- Locks find the game's exe by themselves: the running game, Steam libraries, Battle.net, the Riot Client and the Epic launcher
- "Which server am I on?" names the Overwatch datacenter and checks it against your lock
- Overwatch "Play on it" locks the best server's whole region (e.g. Dammam + Doha + Bahrain), so matches are found as quickly as without a lock
- Overwatch locks never touch port 3724, Blizzard's own port
- Overwatch: every block of Blizzard's network is now placed under its datacenter (a match on an unlisted Chicago server slipped past a Tokyo lock), and Paris is its own datacenter
- **"Which server am I on?" updates by itself:** while the game runs, the button shows your server with a coloured dot ("On: Dammam"), checked every 30 s for 10 minutes after you lock or unlock, then every 90 s
- **Problems are found and reported by themselves**, without interrupting the player: playing outside the lock, unknown game servers, a lock whose firewall rules disappeared, Windows Firewall off, a lock that failed, long queues after locking, datacenters that don't answer, failed downloads and checks, heavy packet loss, slow start-up, crashes and failed updates. Each one is sent once per session and saved in issues.txt; the crash popup is gone
- **New logo ("Quiet Signal"):** a glowing core sending out mirrored ping waves, with one gold spark; drawn separately for each size so it stays clear down to the 16 px tray icon (docs/brand)
- **New look:** rounded cards, buttons that react to hover and clicks, status chips with a coloured dot, ping shown with a coloured dot, hover highlights in the lists, the lock bar and the server list in one card, more breathing room
- **Developer mode changes the whole app** (five clicks on the logo): DEV badge and a developer strip, a Developer tab with the live statistics (Problems, Online, Statistics, Versions, Devices, Log) and tools (self-test, firewall rules, refresh IP lists, server ranges, test notices, fake update bar, forced update window, test problem, log files, diagnostics, reset first-run), address-block counts in the server list, and every detected problem shown right away
- Fortnite, PUBG, Apex and Rocket League: the app tells you where to pick the region inside the game
- The addresses a game talks to are added to the beta statistics, to build server lists for games without official ones (Arena Breakout, Valorant)

## v0.10.0-beta (2026-10-10)

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
