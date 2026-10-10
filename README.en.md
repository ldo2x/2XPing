<p align="center"><img src="assets/logo.png" width="120" alt="2X Ping"></p>

<h1 align="center">2X Ping</h1>

<p align="center">
<b>Lower ping, better games.</b><br>
Tests your game's servers in seconds, tells you where to play, and locks you to the server you want.<br>
Made by <b>lDo2x</b>
</p>

<p align="center">
<a href="../../releases"><img src="https://img.shields.io/github/v/release/ldo2x/2XPing?include_prereleases&label=version&color=ff6fc8" alt="version"></a>
<a href="../../releases"><img src="https://img.shields.io/github/downloads/ldo2x/2XPing/total?color=b69eff&label=downloads" alt="downloads"></a>
<img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue" alt="platform">
<img src="https://img.shields.io/badge/license-MIT-green" alt="license">
<a href="README.md"><img src="https://img.shields.io/badge/lang-العربية-lightgrey" alt="Arabic"></a>
</p>

<p align="center"><a href="../../releases"><b>⬇️ Download the latest version</b></a> · <a href="docs/2XPing-Guide-AR.pdf"><b>📖 Guide (PDF, Arabic)</b></a></p>

> [!WARNING]
> **Beta version.** 2X Ping is still in development: expect bugs, and features may change.
> Please report problems in [Issues](../../issues).

<p align="center"><img src="docs/screenshot-en.png" alt="2X Ping" width="900"></p>

## Download

1. Open [Releases](../../releases) and download the latest `2XPing-v...zip`.
2. Extract it and run `2XPing.exe` (it asks for administrator rights).
3. If **"Windows protected your PC"** appears, click **More info → Run anyway**. The warning appears because this free app is not code-signed yet.

Requires **Windows 10 or 11**. Nothing to install, and after that updates are one click inside the app.

## Features

| Feature | What it does |
|---|---|
| ✦ Best server in one click | Suggests the lowest ping with enough players; **Play on it** locks you to it |
| 📶 Ping test | Ping, jitter and packet loss per server, grouped by region |
| 🔒 Server picker | **Deadlock, CS2, Dota 2**: tick the regions you want and lock out the rest |
| 👥 Player numbers | Estimated players per region and expected time to find a match (Deadlock) |
| ✅ Which server am I on? | While in a match, tells you which server you are on and whether it is one you picked |
| 🎛️ Your way | Pick your games on first launch; Simple layout for everyday players, Advanced with every tool |
| 🕒 Next to the clock | Minimize it while you play: the lock keeps working and the tray icon shows your live ping |
| 💬 No annoying popups | Notices show up like game-chat lines and fade out by themselves |
| ⚡ One-click updates | Downloads the new version, checks its SHA256 and reopens in seconds |
| 🚀 Relay BOOST (Advanced) | Routes only the game's traffic through your own VPS when that path is faster |
| 🌐 Arabic & English | Full right-to-left Arabic interface |

### Supported games
Deadlock · Counter-Strike 2 · Dota 2 · Overwatch 2 · Arena Breakout: Infinite · Valorant · Fortnite · PUBG: Battlegrounds · Apex Legends · Rocket League · Dead by Daylight

## How to use

- **Ping test:** pick a game and every server is tested at once in 2-3 seconds (or press **Test ping**). Green is great (under 60 ms), yellow is OK, red is high.
- **Server picker (Valve games):** tick the regions, press **Lock my servers**, restart the game, then queue. Minimize 2X Ping while you play; it waits next to the clock. 2X Ping blocks the other regions' Valve servers in Windows Firewall. The lock is removed when you press **Unlock** or close the app.
- **Which server am I on?:** join a real match (not practice) and press **Which server am I on?**. 2X Ping watches the game's traffic for 15 seconds and names the server, with a ✓ if it is one you picked. For other games it saves the server it finds.
- **Relay BOOST (optional):**
  1. Rent an Ubuntu VPS near the game servers and run `sudo bash setup-relay.sh` on it.
  2. Install [WireGuard for Windows](https://www.wireguard.com/install/).
  3. Switch to the **Advanced** layout in **Settings**, then add the printed `client.conf` under **Relays**.
  4. Leave the route on **Auto** and press **BOOST**.

## Notes
- Player numbers and match times are **estimates** based on public data.
- **Updates are required during the beta:** when a new version is out a bar appears, and you have 3 days to update with one click. After that the old version stops until you update.
- **Beta statistics:** while open, the app sends the name you enter on first launch, your computer's name, your city, country and internet provider, your PC's hardware and setup, the game, server and ping you use, ping-test results, which features you use, and error logs, to improve the beta. You are told on first launch and accepting is required to use the beta. Details in [SECURITY.md](SECURITY.md).
- 2X Ping **never touches game files or memory**. Everything it changes (firewall rules, tunnel) is undone when you stop it or close the app. See [SECURITY.md](SECURITY.md).
- Some games have their own anti-cheat policies; use the app at your own risk.

## Build from source
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # dist\2XPing.exe
powershell -ExecutionPolicy Bypass -File build.ps1 -Release   # release\2XPing-v<version>.zip
```
Uses the C# compiler that ships with Windows (.NET Framework 4.8), so there is nothing to install.

## 🧪 Beta program: how to help
1. Try 2X Ping with your favourite game.
2. In the app press **Feedback** to report a bug or suggest an idea.
3. **Copy diagnostic info** copies your version and recent log (no personal data). Paste it into your report.

What's next: [ROADMAP.md](ROADMAP.md)

### Known issues
- Windows shows a SmartScreen warning because the app is not code-signed yet.
- Region lock has no effect if Windows Firewall is off or replaced by another security suite.
- Which server am I on? only works while you are **inside** a match or raid (not the menu or practice).
- Per-region player numbers exist for Deadlock only; other games show the Steam total.

## 💬 Contact
| Channel | For |
|---|---|
| [Issues](../../issues/new/choose) | Bug reports |
| [Discussions](../../discussions) | Questions, ideas, new game requests |
| Discord: **`2x.x`** | Talk to the developer |

Contributing: [CONTRIBUTING.md](CONTRIBUTING.md) · Security & privacy: [SECURITY.md](SECURITY.md)

## License
[MIT](LICENSE) © 2026 **lDo2x**. Free and open source; keep the copyright notice when you reuse it.

Font: [Readex Pro](https://fonts.google.com/specimen/Readex+Pro), [SIL Open Font License](assets/fonts/OFL.txt).

<sub>2X Ping is an independent project, not affiliated with Valve, Tencent or any game company. Game names are trademarks of their owners.</sub>
