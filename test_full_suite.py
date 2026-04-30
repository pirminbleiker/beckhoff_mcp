"""Volltest aller MCP-Tools gegen die 3 entdeckten PLC-Targets."""
import json
import subprocess
import sys
import time
from pathlib import Path

EXE = Path(r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe")


class McpClient:
    def __init__(self):
        self.proc = subprocess.Popen(
            [str(EXE)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", cwd=str(EXE.parent),
            bufsize=1,
        )
        self._id = 1
        self._send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "full-test", "version": "0.1"}}})
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        self._read(1)

    def _send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def _read(self, want_id, timeout=15.0):
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

    def call(self, name, args=None, timeout=20.0):
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


PASS = "✓"
FAIL = "✗"
SKIP = "·"


def status(name, ok, detail="", skip=False):
    sym = SKIP if skip else (PASS if ok else FAIL)
    s = f"  {sym} {name:<36} {detail}"
    if len(s) > 130:
        s = s[:130] + "..."
    print(s)


def short(obj, n=80):
    s = json.dumps(obj, default=str)
    return s if len(s) <= n else s[:n] + "..."


def truthy(obj, *keys):
    """Returns whether obj has all keys non-null/non-empty."""
    if not isinstance(obj, dict):
        return False
    for k in keys:
        v = obj.get(k)
        if v is None or v == "" or v == [] or v == {}:
            return False
    return True


def run_target(label, connect_args, sample_path, port_851_active=True):
    print()
    print("=" * 78)
    print(f" {label}")
    print("=" * 78)
    c = McpClient()
    counts = {"pass": 0, "fail": 0, "skip": 0}

    def record(name, ok, detail="", skip=False):
        if skip: counts["skip"] += 1
        elif ok: counts["pass"] += 1
        else:    counts["fail"] += 1
        status(name, ok, detail, skip=skip)

    try:
        # === Connect & Status ===
        r = c.call("beckhoff_connect", connect_args)
        rt_ok = (r.get("runtime") or {}).get("ok", False)
        ss_ok = (r.get("system_service") or {}).get("ok", False)
        record("connect", r.get("success") is True, f"runtime={rt_ok} sys={ss_ok}")

        r = c.call("beckhoff_connection_status")
        record("connection_status", r.get("connected") is True,
               f"target={r.get('target_net_id')}:{r.get('target_port')}")

        # === Device — both default port and SystemService override ===
        r = c.call("beckhoff_get_device_info")
        record("get_device_info(default)", r.get("ok") is True,
               f"name={r.get('name')} v={r.get('version')}")
        r = c.call("beckhoff_get_device_info", {"port": 10000})
        record("get_device_info(port=10000)", r.get("ok") is True,
               f"sys_name={r.get('name')}")

        r = c.call("beckhoff_get_device_state")
        if r.get("ok"):
            record("get_device_state(default)", True, f"ads_state={r.get('ads_state')}")
        else:
            record("get_device_state(default)", False,
                   f"error={r.get('error')} (expected when port 851 inactive)")

        r = c.call("beckhoff_get_device_state", {"port": 10000})
        record("get_device_state(port=10000)", r.get("ok") is True,
               f"sys_state={r.get('ads_state')}")

        # === Symbols ===
        r = c.call("beckhoff_list_symbols", {"limit": 3})
        total = r.get("total", 0)
        record("list_symbols", "symbols" in r,
               f"total={total} count={r.get('count')} mode={r.get('mode')}")

        if total == 0 and not sample_path:
            record("list_symbols regex", False, "no symbols loaded — skipping symbol tests", skip=True)
            record("get_symbol_info", False, "no symbol", skip=True)
            record("read_variable", False, "no symbol", skip=True)
            record("read_variables(explicit)", False, "no symbol", skip=True)
            record("read_variables(regex)", False, "no symbol", skip=True)
            record("get_type_info", False, "no symbol", skip=True)
            record("dereference", False, "no symbol", skip=True)
            record("get_rpc_methods", False, "no symbol", skip=True)
            record("trace_start", False, "no symbol", skip=True)
            record("trace_get(summary)", False, "no symbol", skip=True)
            record("trace_stop", False, "no symbol", skip=True)
        else:
            if total > 0 and r.get("symbols"):
                sample_path = sample_path or r["symbols"][0]["name"]
            namespace = sample_path.split(".")[0] if sample_path and "." in sample_path else None

            # New: regex search
            r = c.call("beckhoff_list_symbols", {"pattern": r"\.c[A-Z][a-z].*", "limit": 5})
            ok = r.get("mode") == "regex" and r.get("count", 0) > 0
            record("list_symbols(regex)", ok,
                   f"matches={r.get('count')} (pattern '\\.c[A-Z][a-z].*')")

            # New: regex + parent_path
            if namespace:
                r = c.call("beckhoff_list_symbols",
                           {"pattern": r".*Global$", "parent_path": namespace, "limit": 5})
                ok = r.get("mode") == "regex" and "symbols" in r
                record("list_symbols(regex+parent)", ok,
                       f"matches={r.get('count')} parent={namespace}")

            r = c.call("beckhoff_get_symbol_info", {"symbol_name": sample_path})
            record("get_symbol_info", "type" in r, f"type={r.get('type')}")

            r = c.call("beckhoff_read_variable", {"symbol_name": sample_path})
            record("read_variable", r.get("ok") is True, f"value={r.get('value')}")

            r = c.call("beckhoff_read_variables", {"symbol_names": [sample_path]})
            ok = r.get("results", [{}])[0].get("ok") is True
            record("read_variables(explicit)", ok,
                   f"mode={r.get('mode')} success={r.get('success_count')}")

            # New: read_variables with regex
            if namespace:
                r = c.call("beckhoff_read_variables",
                           {"pattern": r"^" + namespace + r"\.c", "max_results": 3})
                ok = r.get("mode") == "regex" and r.get("success_count", 0) > 0
                record("read_variables(regex)", ok,
                       f"matched={r.get('matched_count')} success={r.get('success_count')}")

            r = c.call("beckhoff_get_type_info", {"type_or_symbol_path": sample_path})
            record("get_type_info", r.get("ok") is True,
                   f"name={r.get('type_name')} cat={r.get('category')}")

            r = c.call("beckhoff_dereference", {"symbol_path": sample_path})
            record("dereference", r.get("ok") is True,
                   f"category={r.get('category')} is_null={r.get('is_null')}")

            r = c.call("beckhoff_get_rpc_methods", {"symbol_path": sample_path})
            ok = r.get("ok") is True or "error" in r  # legitimate error for primitive
            record("get_rpc_methods", ok, short(r, 80))

            # === Trace (only when port 851 is active) ===
            if port_851_active:
                r = c.call("beckhoff_trace_start",
                           {"paths": sample_path, "mode": "onChange",
                            "cycle_time_ms": 100, "max_duration_ms": 2000})
                ok = r.get("ok") is True
                tid = r.get("trace_id")
                record("trace_start", ok, f"trace_id={tid} vars={r.get('variable_count')}")
                if ok and tid:
                    time.sleep(1.5)
                    r = c.call("beckhoff_trace_get", {"trace_id": tid, "format": "summary"})
                    record("trace_get(summary)", r.get("ok") is True,
                           f"events={(r.get('session_info') or {}).get('eventCount')}")
                    r = c.call("beckhoff_trace_stop", {"trace_id": tid})
                    record("trace_stop", r.get("ok") is True,
                           f"events={r.get('event_count')}")
            else:
                record("trace_start", False, "port 851 inactive", skip=True)
                record("trace_get", False, "port 851 inactive", skip=True)
                record("trace_stop", False, "port 851 inactive", skip=True)

        # === Port queries ===
        port = connect_args.get("target_port", 851)
        r = c.call("beckhoff_query_ads_port", {"ads_port": port})
        ok = r.get("success") is True
        record(f"query_ads_port({port})", ok,
               f"name={(r.get('device_info') or {}).get('name')}")

        r = c.call("beckhoff_query_ads_port", {"ads_port": 10000})
        record("query_ads_port(10000)", r.get("success") is True,
               f"name={(r.get('device_info') or {}).get('name')}")

        if sample_path:
            r = c.call("beckhoff_read_from_port",
                       {"ads_port": port, "symbol_name": sample_path})
            record(f"read_from_port({port})", r.get("value") is not None or r.get("ok"),
                   f"value={r.get('value')}")
        else:
            record("read_from_port", False, "no symbol", skip=True)

    finally:
        c.close()

    print(f"\n  ↳ {counts['pass']} pass · {counts['fail']} fail · {counts['skip']} skip")
    return counts


# === Main ===
print(f"Boot {EXE.name} — full tool suite against all targets")

agg = {"pass": 0, "fail": 0, "skip": 0}

for ctx in [
    dict(label="Target 1: REMOTE PLC  169.254.34.222.1.1  (broker 192.168.71.38)",
         connect_args={"target_net_id": "169.254.34.222.1.1", "target_port": 851,
                       "mqtt_broker": "192.168.71.38", "mqtt_port": 1883, "mqtt_topic": "AdsOverMqtt"},
         sample_path="BaseMsg.cClusterGlobal", port_851_active=True),
    dict(label="Target 2: LOCAL DOCKER PLC  175.57.15.0.1.1  (broker 127.0.0.1)",
         connect_args={"target_net_id": "175.57.15.0.1.1", "target_port": 851,
                       "mqtt_broker": "127.0.0.1", "mqtt_port": 1883, "mqtt_topic": "AdsOverMqtt"},
         sample_path=None, port_851_active=False),
    dict(label="Target 3: ENGINEERING HOST  172.18.164.255.1.1  (broker 192.168.71.38)",
         connect_args={"target_net_id": "172.18.164.255.1.1", "target_port": 851,
                       "mqtt_broker": "192.168.71.38", "mqtt_port": 1883, "mqtt_topic": "AdsOverMqtt"},
         sample_path=None, port_851_active=True),
]:
    res = run_target(**ctx)
    agg["pass"] += res["pass"]
    agg["fail"] += res["fail"]
    agg["skip"] += res["skip"]

print()
print("=" * 78)
print(f"  TOTAL: {agg['pass']} pass · {agg['fail']} fail · {agg['skip']} skip")
print("=" * 78)
sys.exit(0 if agg["fail"] == 0 else 1)
