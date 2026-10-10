<p align="center"><img src="assets/logo.png" width="120" alt="2X Ping"></p>

<h1 align="center">2X Ping</h1>

<p align="center">
Game connection optimizer: measures ping, picks the best server and route, and lets you choose where you play.<br>
Made by <b>D7oom2x</b>
</p>

<p align="center">
<img src="https://img.shields.io/badge/version-0.9.0--beta-orange" alt="version">
<img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue" alt="platform">
<img src="https://img.shields.io/badge/license-MIT-green" alt="license">
<a href="README.md"><img src="https://img.shields.io/badge/lang-العربية-lightgrey" alt="Arabic"></a>
</p>

> [!WARNING]
> **Beta version.** 2X Ping is still in development: expect bugs, and features may change.
> Please report problems in [Issues](../../issues).

<p align="center"><img src="docs/screenshot-en.png" alt="2X Ping" width="900"></p>

## Download

1. Open [Releases](../../releases) and download the latest `2XPing-v...zip`.
2. Extract it and run `2XPing.exe` (it asks for administrator rights).
3. If **"Windows protected your PC"** appears, click **More info → Run anyway**. The warning appears because this free app is not code-signed yet.

Requires **Windows 10 or 11**. Nothing to install.

## Features

| Feature | What it does |
|---|---|
| 📶 Ping test | Ping, jitter and packet loss per server region, closest first |
| 🔒 Server picker | **Deadlock, CS2, Dota 2**: tick the regions you want and lock out the rest |
| 👥 Player numbers | Estimated players per region and expected time to find a match (Deadlock) |
| 🔍 Detect my server | Finds the server you are playing on right now, and its location (any game) |
| 🚀 Relay BOOST | Routes only the game's traffic through your own VPS when that path is faster |
| 📈 Live ping | Live ping and packet-loss graph while you play |
| 🌐 Arabic & English | Full right-to-left Arabic interface |

### Supported games
Deadlock · Counter-Strike 2 · Dota 2 · Arena Breakout: Infinite · Valorant · Fortnite · PUBG: Battlegrounds · Apex Legends · Rocket League · Dead by Daylight, **plus any other game** through custom or detected servers.

## How to use

- **Ping test:** pick a game and press **Test ping**. Green is great (under 60 ms), yellow is OK, red is high.
- **Server picker (Valve games):** tick the regions, press **Lock servers**, restart the game, then queue. 2X Ping blocks the other regions' Valve servers in Windows Firewall. The lock is removed when you press **Unlock** or close the app.
- **Detect my server:** join a match or raid, then press **Detect my server**. 2X Ping watches the game's traffic for 15 seconds and saves the server it finds.
- **Relay BOOST (optional):**
  1. Rent an Ubuntu VPS near the game servers and run `sudo bash setup-relay.sh` on it.
  2. Install [WireGuard for Windows](https://www.wireguard.com/install/).
  3. Add the printed `client.conf` under **Relays**.
  4. Leave the route on **Auto** and press **BOOST**.

## Notes
- Player numbers and match times are **estimates** based on public data.
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
- Detect my server only works while you are **inside** a match or raid.
- Per-region player numbers exist for Deadlock only; other games show the Steam total.

## 💬 Contact
| Channel | For |
|---|---|
| [Issues](../../issues/new/choose) | Bug reports |
| [Discussions](../../discussions) | Questions, ideas, new game requests |
| Discord: **`2x.x`** | Talk to the developer |

Contributing: [CONTRIBUTING.md](CONTRIBUTING.md) · Security & privacy: [SECURITY.md](SECURITY.md)

## License
[MIT](LICENSE) © 2026 **D7oom2x**. Free and open source; keep the copyright notice when you reuse it.

<sub>2X Ping is an independent project, not affiliated with Valve, Tencent or any game company. Game names are trademarks of their owners.</sub>
