#!/usr/bin/env bash
# 2X Ping (by D7oom2x) relay setup for a Ubuntu/Debian VPS.
# Installs WireGuard (game traffic relay) + a small latency agent (lets the app measure relay -> game server ping).
# Usage:  sudo bash setup-relay.sh
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo bash setup-relay.sh)"; exit 1; }

WG_PORT=${WG_PORT:-51820}
AGENT_PORT=${AGENT_PORT:-8787}

export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y wireguard iptables python3 curl iputils-ping

IFACE=$(ip -4 route show default | awk '{print $5; exit}')
PUBIP=$(curl -4 -s --max-time 5 https://api.ipify.org || hostname -I | awk '{print $1}')

mkdir -p /etc/wireguard
cd /etc/wireguard
umask 077
[ -f server.key ] || { wg genkey | tee server.key | wg pubkey > server.pub; }
[ -f client.key ] || { wg genkey | tee client.key | wg pubkey > client.pub; }

cat > wg0.conf <<EOF
[Interface]
Address = 10.66.66.1/24
ListenPort = $WG_PORT
PrivateKey = $(cat server.key)
PostUp = iptables -t nat -A POSTROUTING -o $IFACE -j MASQUERADE; iptables -A FORWARD -i wg0 -j ACCEPT; iptables -A FORWARD -o wg0 -j ACCEPT
PostDown = iptables -t nat -D POSTROUTING -o $IFACE -j MASQUERADE; iptables -D FORWARD -i wg0 -j ACCEPT; iptables -D FORWARD -o wg0 -j ACCEPT

[Peer]
PublicKey = $(cat client.pub)
AllowedIPs = 10.66.66.2/32
EOF

cat > client.conf <<EOF
[Interface]
PrivateKey = $(cat client.key)
Address = 10.66.66.2/32
MTU = 1420

[Peer]
PublicKey = $(cat server.pub)
Endpoint = $PUBIP:$WG_PORT
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
EOF

echo "net.ipv4.ip_forward=1" > /etc/sysctl.d/99-2xping.conf
sysctl --system >/dev/null
systemctl enable wg-quick@wg0 >/dev/null 2>&1
systemctl restart wg-quick@wg0

# ---- latency agent ----
cat > /opt/2xping-agent.py <<'PY'
#!/usr/bin/env python3
import os, re, socket, subprocess, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

TOKEN = os.environ.get("PB_TOKEN", "")
PORT = int(os.environ.get("PB_PORT", "8787"))
HOST_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9.\-]{0,252}")

def tcp_ping(host, port, n):
    ip = socket.gethostbyname(host)
    out = []
    for _ in range(n):
        t = time.perf_counter()
        try:
            with socket.create_connection((ip, port), timeout=2):
                out.append((time.perf_counter() - t) * 1000)
        except OSError:
            pass
        time.sleep(0.05)
    return sum(out) / len(out) if out else -1

def icmp_ping(host, n):
    try:
        out = subprocess.run(["ping", "-c", str(n), "-i", "0.2", "-W", "1", host],
                             capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return -1
    m = re.search(r"= [\d.]+/([\d.]+)/", out)
    return float(m.group(1)) if m else -1

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if u.path != "/probe" or not TOKEN or q.get("token", [""])[0] != TOKEN:
            self.send_response(403); self.end_headers(); return
        host = q.get("host", [""])[0]
        try:
            port = int(q.get("port", ["0"])[0]); n = max(1, min(int(q.get("n", ["3"])[0]), 10))
        except ValueError:
            self.send_response(400); self.end_headers(); return
        if not HOST_RE.fullmatch(host) or not 0 <= port <= 65535:
            self.send_response(400); self.end_headers(); return
        try:
            v = icmp_ping(host, n) if port == 0 else tcp_ping(host, port, n)
        except Exception:
            v = -1
        body = ("%.1f" % v).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass

ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
PY

TOKEN=$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')
cat > /etc/systemd/system/2xping-agent.service <<EOF
[Unit]
Description=2X Ping latency agent (D7oom2x)
After=network-online.target

[Service]
Environment=PB_TOKEN=$TOKEN
Environment=PB_PORT=$AGENT_PORT
ExecStart=/usr/bin/python3 /opt/2xping-agent.py
Restart=always
DynamicUser=yes
AmbientCapabilities=CAP_NET_RAW

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable 2xping-agent >/dev/null 2>&1
systemctl restart 2xping-agent

if command -v ufw >/dev/null && ufw status | grep -q active; then
  ufw allow "$WG_PORT"/udp
  ufw allow "$AGENT_PORT"/tcp
fi

echo
echo "========== 2X Ping relay ready (by D7oom2x) =========="
echo "1) Copy this file to your PC as  <name>.conf :"
echo "-------------------------------------------------------"
cat /etc/wireguard/client.conf
echo "-------------------------------------------------------"
echo "2) In 2X Ping > Relays > Add relay, use:"
echo "   Agent URL : http://$PUBIP:$AGENT_PORT"
echo "   Token     : $TOKEN"
echo "Also open UDP $WG_PORT and TCP $AGENT_PORT in your VPS provider's firewall if it has one."
