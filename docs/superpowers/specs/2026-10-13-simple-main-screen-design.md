# Simple main screen: one card, clear choices

Date: 2026-10-13 · Status: approved direction (A), awaiting spec review

## Why

Beta testers keep asking four things: what to press first, whether the lock worked, why the ping differs
between the app and the game, and what the words mean (relay, blocked, codes like `[iad]`). The current
main screen shows a status chip, a best-server card, a "Which server am I on?" button, a player-numbers
line, a long lock line, a 30-row server list and a status bar at once, and repeats "locked on X" in four
places with different ping numbers.

**Success:** a first-time player picks a region, plays, and can see that the lock works, without asking
anyone. Testers stop asking those four questions.

## Decisions (from the user)

- Direction A: one state card in the middle that answers one question at a time.
- Deadlock players often trade ping for a faster queue, so the card offers 2-3 regions with **ping and
  queue speed** side by side, and the player chooses. More than one region can be chosen.
- Games that can't be locked (Arena Breakout) show the server the player is on right now.
- Players per region: a word (lots / some / few) **and** an approximate number, inside each choice.
- Overwatch is chosen by **region** (Middle East = Dammam + Doha + Bahrain, ...), not by datacenter.
- The detailed server list stays reachable in the simple layout through a small "اختيار يدوي" link.
- The advanced layout gets the same card on top, with the full list, tabs and tools under it.

## Layout (simple)

Kept: the game list, the top bar (logo, Settings, Contact).

Removed from the simple layout (all stay in advanced): the server list (behind "اختيار يدوي"), the lock bar
and its long line, the player-numbers line, the "Which server am I on?" button (the automatic check feeds the
card), the status chip and the bottom status text (the card says it).

Added: the **state card**, filling the centre.

## The state card

One state at a time:

| State | Text | Action |
|---|---|---|
| Loading | «أجيب سيرفرات {game}...» | none |
| Testing | «أدوّر لك أحسن منطقة...» | none |
| Choose (lockable games) | 2-3 region tiles (below) | «العب على {regions}» |
| Locked, no match yet | «✓ مثبّت على {regions}» + «افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول.» | «فك التثبيت» |
| In a match, on a picked region | «تلعب الحين في {region} ✓» + «التثبيت شغّال. البنق داخل اللعبة هو الأدق.» | «فك التثبيت» |
| In a match through a Valve relay | «تلعب الحين في واحد من مناطقك ✓ ({regions})» + same sub-line | «فك التثبيت» |
| In a match outside the lock | gold: «اللعبة حطّتك على {region}» + «اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة.» | «فك التثبيت» |
| Not lockable, not in a match | «أحسن سيرفر لك: {server} · بنقك تقريباً {ms}» + «اختاره من داخل اللعبة.» | none |
| Not lockable, in a match | «إنت الحين على: {server} · بنقك {ms}» | none |
| No internet / load failed | «ما قدرت أوصل للسيرفرات. شيّك على النت.» | «جرّب مرة ثانية» |

Rules:
- One ping number on screen at a time; while in a match the card says the game's number is the accurate one.
- No "relay", "blocked", codes or "ms" jargon in the simple layout beyond the ping number itself.
- The card changes on its own from the automatic check (every 30 s for 10 minutes after a lock change,
  then every 90 s, paused while the game is in front, refreshed on return).

### Region tiles (Choose state)

- Up to 3 tiles side by side: the best ping, the fastest queue (when queue data exists), and the next best
  ping; duplicates collapse, so 2 tiles is normal. Every other region sits under «مناطق ثانية».
- Each tile: region name, «بنق {ms}», «الانتظار {wait}» (Deadlock only), «لاعبين كثير/متوسط/قليل · {n}»
  (Deadlock only), and one tag: «أحسن خيار» (best ping and fastest queue), «أقل بنق», or «أسرع قيم».
- Tiles toggle (several can be on); the best one starts selected. The main button names the selection:
  «العب على أوروبا» or «العب على أوروبا وروسيا», with a sub-line «البنق بين 78 و110، والبحث أسرع» when
  several are on.
- Pressing the button locks every datacenter of the selected regions (only datacenters that host matches).

## Regions per game

A region's ping is the best ping among its datacenters; its players and wait come from its match data.

- **Deadlock:** the matchmaking regions in `Stats.MmRegions` (Europe, North America, SE Asia, Russia,
  Oceania, South America), datacenters by `Stats.PopToMm`. Relay-only locations are never shown.
- **Overwatch 2:** Middle East (GMEC2, GMEC1, MES1), Europe (GEW3, AMS1, CDG1, GEN1), North America
  (GUE4, ORD1, LAX1), South America (GBR1), East Asia (ICN1, GTK1, TPE1), Singapore (GSG1), Australia (SYD2).
  No player data, so tiles show ping only and the tag is «أقل بنق».
- **Dead by Daylight:** Europe (eu-central-1, eu-west-1, eu-west-2), North America (us-east-1, us-east-2,
  ca-central-1, us-west-1, us-west-2), India (ap-south-1), East Asia (ap-northeast-1, ap-northeast-2,
  ap-east-1), Singapore (ap-southeast-1), Australia (ap-southeast-2), South America (sa-east-1). Ping only.
- **Arena Breakout:** no regions, no lock; the card shows the best server and, in a match, the current one.

The region tables live next to each game in `GameDb` (a `Regions` grouping per game), so adding a game is
data, not new UI code.

## Manual choice

«اختيار يدوي» (a small link under the tiles in the simple layout) opens the existing server list with its
tick boxes and «ثبّت سيرفراتي», under the card. Picking servers by hand moves the card to the Locked state
with the picked server names. The advanced layout always shows the list under the card.

## Notices

Chat-style notices drop to the ones the player must not miss: Windows Firewall off, the lock rules removed,
an update. Everything else moves into the card text: "try a casual match first", "restart the game", "you're
playing on", "locked".

## Out of scope

Settings, the welcome window, the developer mode and the relay BOOST stay as they are. No change to how the
lock, the ping test or the automatic check work underneath.

## Testing

- Screenshots of every card state in Arabic and English, for each of the four games (harness with fake states).
- A test that builds each game's regions from real data and checks every hosting datacenter belongs to
  exactly one region, and relay-only ones to none.
- A test that choosing two regions locks exactly their datacenters.
- Real-play check by the user: lock from a tile, play a match, see the card change to «تلعب الحين في ...».
