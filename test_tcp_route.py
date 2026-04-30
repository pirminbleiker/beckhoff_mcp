"""TCP-route check for beckhoff-mcp.

Steps:
  1. discover_network on 192.168.71.0/24 to confirm the PLC and its IP
  2. connect with transport='tcp' to that NetId/IP
  3. read a known variable to prove TCP path actually moves data
  4. compare against an MQTT connect to the same target
"""
import json
import subprocess
import time
from pathlib import Path

EXE = Path(r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe")

TARGET_NET_ID = "172.18.164.255.1.1"   # Engineering host VM (route just added by beckhoff_add_route)
TARGET_IP     = "172.23.103.131"
SAMPLE_PATH   = None                   # picked from list_symbols if available
MQTT_BROKER   = "192.168.71.38"


STDERR_LOG = Path(r"D:/Projects/Open Source/beckhoff_mcp/tcp_test_stderr.log")


class McpClient:
    def __init__(self):
        self._stderr_fh = open(STDERR_LOG, "w", encoding="utf-8")
        self.proc = subprocess.Popen(
            [str(EXE)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self._stderr_fh,
            text=True, encoding="utf-8", cwd=str(EXE.parent),
            bufsize=1,
        )
        self._id = 1
        self._send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "tcp-test", "version": "0.1"}}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        self._read(1)

    def _send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def _read(self, want_id, timeout=20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                return None
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if obj.get("id") == want_id:
                return obj
        return None

    def call(self, name, args=None, timeout=25.0):
        self._id += 1
        cid = self._id
        self._send({"jsonrpc": "2.0", "id": cid, "method": "tools/call",
                    "params": {"name": name, "arguments": args or {}}})
        resp = self._read(cid, timeout=timeout)
        if resp is None:
            return {"_no_response": True}
        try:
            return json.loads(resp["result"]["content"][0]["text"])
        except Exception:
            return resp.get("result", {})

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=4)
        except Exception:
            self.proc.kill()
        try:
            self._stderr_fh.close()
        except Exception:
            pass


def header(s):
    print()
    print("=" * 78)
    print(f"  {s}")
    print("=" * 78)


def short(obj, n=140):
    s = json.dumps(obj, default=str)
    return s if len(s) <= n else s[:n] + "..."


c = McpClient()
try:
    header("1. discover_network — confirm engineering-host IP for the chosen NetId")
    r = c.call("beckhoff_discover_network",
               {"subnets": ["172.23.96.0/20"], "udp_timeout_ms": 600,
                "tcp_timeout_ms": 800, "max_parallelism": 128},
               timeout=180.0)
    matched = [h for h in r.get("hosts", []) if h.get("ams_net_id") == TARGET_NET_ID]
    print(f"  scanned={r.get('scanned_count')} hosts={r.get('host_count')}  match for {TARGET_NET_ID}: {len(matched)}")
    for h in matched:
        print(f"    ip={h['ip']} hostname={h.get('hostname')} tc={h.get('twincat_version')} ports={[p['port'] for p in h.get('open_tcp', [])]}")
    if not matched:
        # Fallback: scan all known Beckhoff-shaped hosts that expose port 48898
        candidates = [h for h in r.get("hosts", []) if any(p["port"] == 48898 for p in h.get("open_tcp", []))]
        print(f"  ! NetId not found in discovery — candidates with port 48898: {[(h['ip'], h.get('ams_net_id'), h.get('hostname')) for h in candidates]}")
    if matched:
        TARGET_IP = matched[0]["ip"]
    has_tcp_port = bool(matched and any(p["port"] == 48898 for p in matched[0].get("open_tcp", [])))
    print(f"  TCP/48898 reachable on host: {has_tcp_port}")
    print(f"  → TARGET_IP = {TARGET_IP}")

    if TARGET_IP is None:
        print("  ! No target IP — aborting TCP test.")
        raise SystemExit(1)

    header("2. connect via TCP transport (10s probe timeout)")
    t0 = time.time()
    r = c.call("beckhoff_connect", {
        "target_net_id": TARGET_NET_ID,
        "target_port": 851,
        "transport": "tcp",
        "target_ip": TARGET_IP,
        "probe_timeout_ms": 10000,
    }, timeout=30.0)
    print(f"  elapsed={time.time()-t0:.1f}s")
    print(f"  success={r.get('success')}  transport={r.get('transport')}  router_running={r.get('tcp_router_running')}")
    print(f"  local_net_id={r.get('local_net_id')}  local_name={r.get('local_name')}")
    if r.get("backroute_hint"):
        print(f"  backroute_hint: {r['backroute_hint']}")
    print(f"  runtime: {short(r.get('runtime'), 200)}")
    print(f"  system_service: {short(r.get('system_service'), 200)}")
    if not r.get("success"):
        print(f"  error: {r.get('error')}")
        print(f"  hint: {r.get('hint')}")

    header("3. list a few symbols over the TCP path")
    r = c.call("beckhoff_list_symbols", {"limit": 5}, timeout=20.0)
    syms = r.get("symbols", []) or []
    print(f"  list_symbols mode={r.get('mode')} total={r.get('total')} count={r.get('count')}")
    for s in syms[:5]:
        print(f"    - {s.get('name')}  type={s.get('type')}")
    if syms and SAMPLE_PATH is None:
        SAMPLE_PATH = syms[0]["name"]

    if SAMPLE_PATH:
        r = c.call("beckhoff_read_variable", {"symbol_name": SAMPLE_PATH}, timeout=15.0)
        print(f"  read_variable {SAMPLE_PATH}: ok={r.get('ok')} value={short(r.get('value'), 80)} error={r.get('error')}")
    else:
        print("  no sample symbol — skipping read_variable")

    header("4. baseline: switch back to MQTT to confirm the same target works that way")
    r = c.call("beckhoff_connect", {
        "target_net_id": TARGET_NET_ID,
        "target_port": 851,
        "transport": "mqtt",
        "mqtt_broker": MQTT_BROKER,
        "mqtt_port": 1883,
        "mqtt_topic": "AdsOverMqtt",
    }, timeout=30.0)
    print(f"  success={r.get('success')}  transport={r.get('transport')}")
    print(f"  runtime: {short(r.get('runtime'), 200)}")

    if SAMPLE_PATH:
        r = c.call("beckhoff_read_variable", {"symbol_name": SAMPLE_PATH}, timeout=15.0)
        print(f"  read_variable (mqtt): ok={r.get('ok')} value={short(r.get('value'), 80)} error={r.get('error')}")
finally:
    c.close()
