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

## Data sent to other services

| Service | What is sent | Why |
|---|---|---|
| Steam Web API | Game app id | Online player count, Valve server list |
| deadlock-api.com | A fixed statistics query | Deadlock players per region |
| ip-ranges.amazonaws.com | Nothing (download only) | AWS game server IP ranges |
| ip-api.com | The **game server** IP you detected | Show the server's country / city |
| api.github.com | Nothing (download only) | Check for a newer version |
| Your own relay agent | Game server address + your token | Measure relay -> server ping |

No personal data, account data or usage tracking is collected.

## Relay notes

- WireGuard configs contain private keys. They are stored in `%APPDATA%\2XPing` and must never be shared or committed.
- The relay latency agent uses plain HTTP protected by a random token. Keep the token private.
