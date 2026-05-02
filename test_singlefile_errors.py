"""Capture stderr to see why MEF discovery fails in single-file build."""
import json, subprocess, time
from pathlib import Path

EXE = Path(r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish-single/beckhoff-mcp.exe")
LOG = Path(r"D:/Projects/Open Source/beckhoff_mcp/singlefile_stderr.log")

with open(LOG, "w", encoding="utf-8") as fh:
    proc = subprocess.Popen(
        [str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=fh,
        text=True, encoding="utf-8", cwd=str(EXE.parent), bufsize=1,
    )
    proc.stdin.write(json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                   "clientInfo": {"name": "smoke", "version": "0.1"}}
    }) + "\n"); proc.stdin.flush()
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); proc.stdin.flush()
    proc.stdin.write(json.dumps({
        "jsonrpc": "2.0", "id": 2, "method": "tools/call",
        "params": {"name": "beckhoff_connect",
                   "arguments": {"target_net_id": "169.254.34.222.1.1", "target_port": 851,
                                 "mqtt_broker": "192.168.71.38", "mqtt_port": 1883,
                                 "mqtt_topic": "AdsOverMqtt", "probe_timeout_ms": 4000}}
    }) + "\n"); proc.stdin.flush()
    deadline = time.time() + 30.0
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line: continue
        obj = json.loads(line)
        if obj.get("id") == 2:
            print(json.dumps(obj.get("result"), indent=2)[:1200])
            break
    proc.stdin.close()
    try: proc.wait(timeout=4)
    except Exception: proc.kill()
print(f"\nstderr → {LOG}")
