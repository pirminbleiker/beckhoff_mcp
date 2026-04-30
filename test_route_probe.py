"""Tests beckhoff_add_route.

  python test_route_probe.py            → dry_run probe of both targets
  python test_route_probe.py linux      → interactive add-route on Linux PLC
  python test_route_probe.py vm         → interactive add-route on Engineering VM
                                           (a Windows credential dialog will pop up)
"""
import json
import subprocess
import sys
import time
from pathlib import Path

EXE = Path(r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe")


class McpClient:
    def __init__(self):
        self._stderr = open(r"D:/Projects/Open Source/beckhoff_mcp/route_probe_stderr.log", "w", encoding="utf-8")
        self.proc = subprocess.Popen(
            [str(EXE)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self._stderr,
            text=True, encoding="utf-8", cwd=str(EXE.parent), bufsize=1,
        )
        self._id = 1
        self._send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "route-probe", "version": "0.1"}}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        self._read(1)

    def _send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n"); self.proc.stdin.flush()

    def _read(self, want_id, timeout=15.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line: return None
            try: obj = json.loads(line)
            except json.JSONDecodeError: continue
            if obj.get("id") == want_id: return obj
        return None

    def call(self, name, args=None, timeout=15.0):
        self._id += 1
        self._send({"jsonrpc": "2.0", "id": self._id, "method": "tools/call",
                    "params": {"name": name, "arguments": args or {}}})
        resp = self._read(self._id, timeout=timeout)
        if resp is None: return {"_no_response": True}
        try: return json.loads(resp["result"]["content"][0]["text"])
        except Exception: return resp.get("result", {})

    def close(self):
        try: self.proc.stdin.close(); self.proc.wait(timeout=4)
        except Exception: self.proc.kill()


TARGETS = {
    "linux": ("Linux PLC",      "169.254.34.222.1.1", "192.168.71.38"),
    "vm":    ("Engineering VM", "172.18.164.255.1.1", "172.23.103.131"),
}

mode = sys.argv[1] if len(sys.argv) > 1 else "dry"

c = McpClient()
try:
    if mode == "dry":
        print()
        print("== beckhoff_add_route — DRY probe (no credentials, no real add) ==")
        for label, net_id, ip in TARGETS.values():
            print()
            print(f"--- {label}  {net_id} @ {ip} ---")
            r = c.call("beckhoff_add_route", {
                "target_net_id": net_id, "target_ip": ip,
                "dry_run": True, "timeout_ms": 4000,
            }, timeout=15.0)
            print(json.dumps(r, indent=2, default=str))
    elif mode in TARGETS:
        label, net_id, ip = TARGETS[mode]
        print()
        print(f"== Interactive add-route → {label}  {net_id} @ {ip} ==")
        print(f"A Windows credential dialog will pop up. Enter the PLC's username/password.")
        print(f"Optionally tick 'Save' so the next call reuses the saved cred from the Vault.")
        r = c.call("beckhoff_add_route", {
            "target_net_id": net_id, "target_ip": ip,
            "timeout_ms": 6000,
        }, timeout=300.0)
        print(json.dumps(r, indent=2, default=str))
    else:
        print(f"Unknown mode '{mode}'. Use one of: dry, linux, vm")
        sys.exit(2)
finally:
    c.close()
