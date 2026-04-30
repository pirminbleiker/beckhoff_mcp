"""End-to-End Test aller MCP-Tools des .NET Servers via stdio JSON-RPC."""
import json
import subprocess
import sys
import time
from pathlib import Path

EXE = Path(r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe")


def session(tool_calls: list[tuple[str, dict]]) -> dict[int, dict]:
    """Send initialize + all tool calls, collect responses by id."""
    msgs = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize",
         "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                    "clientInfo": {"name": "test-driver", "version": "0.1"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
    ]
    for i, (tool, args) in enumerate(tool_calls, start=2):
        msgs.append({"jsonrpc": "2.0", "id": i, "method": "tools/call",
                     "params": {"name": tool, "arguments": args}})

    stdin_text = "\n".join(json.dumps(m) for m in msgs) + "\n"

    # Give the server enough wallclock to handle each call
    request_count = sum(1 for m in msgs if "id" in m)
    settle = max(8, 2 * request_count)

    proc = subprocess.Popen(
        [str(EXE)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8",
        cwd=str(EXE.parent),
    )
    proc.stdin.write(stdin_text)
    proc.stdin.flush()
    # Keep stdin open so the server doesn't shut down before flushing
    time.sleep(settle)
    proc.stdin.close()

    responses: dict[int, dict] = {}
    try:
        out, err = proc.communicate(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        out, err = proc.communicate()
    if not out:
        print(f"  >> stdout empty. stderr last 600 chars:\n{(err or '')[-600:]}")

    for line in (out or "").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
            if "id" in obj:
                responses[obj["id"]] = obj
        except json.JSONDecodeError:
            continue
    return responses


def short(obj, limit=300):
    s = json.dumps(obj, indent=2, default=str)
    return s if len(s) <= limit else s[:limit] + " ...(truncated)"


def extract_text(resp):
    if "result" not in resp:
        return resp.get("error", {}).get("message", str(resp))
    content = resp["result"].get("content")
    if not content:
        return resp["result"]
    text = content[0].get("text", "")
    try:
        return json.loads(text)
    except Exception:
        return text


def main():
    print(f"=== Booting {EXE.name} for combined tool exercise ===\n")

    # Phase 0: discover an actual symbol so later tests have realistic args
    bootstrap_resps = session([
        ("beckhoff_get_device_info", {}),
        ("beckhoff_list_symbols", {"limit": 50}),
    ])
    print(f"  bootstrap responses received: ids={sorted(bootstrap_resps.keys())}")
    if 2 not in bootstrap_resps or 3 not in bootstrap_resps:
        print(f"  bootstrap incomplete; raw = {bootstrap_resps}")
        return 1
    info = extract_text(bootstrap_resps[2])
    syms = extract_text(bootstrap_resps[3])
    print(f"PLC: {info}")
    print(f"List: {syms.get('count')}/{syms.get('total')} symbols listed.\n")

    sample_symbol = None
    for s in syms.get("symbols", []):
        if s["name"]:
            sample_symbol = s["name"]
            break
    print(f"Picked sample symbol: {sample_symbol!r}\n")

    # Run-1: all non-EtherCAT tools (PLC has no EtherCAT master, separate run)
    tools = [
        ("beckhoff_get_device_info", {}),
        ("beckhoff_get_device_state", {}),
        ("beckhoff_connection_status", {}),
        ("beckhoff_list_symbols", {"limit": 3}),
        ("beckhoff_get_symbol_info", {"symbol_name": sample_symbol or "MAIN"}),
        ("beckhoff_read_variable", {"symbol_name": sample_symbol or "MAIN"}),
        ("beckhoff_read_variables", {"symbol_names": [sample_symbol or "MAIN"]}),
        ("beckhoff_discover_and_connect",
         {"target_net_id": "169.254.34.222.1.1", "target_port": 851}),
        ("beckhoff_query_ads_port", {"ads_port": 851}),
        ("beckhoff_read_from_port",
         {"ads_port": 851, "symbol_name": sample_symbol or "MAIN"}),
    ]

    resps = session(tools)

    print(f"=== Results ({len(resps)-1}/{len(tools)} responses received) ===\n")
    ok = 0
    fail = 0
    for i, (tool, args) in enumerate(tools, start=2):
        r = resps.get(i)
        if r is None:
            print(f"[?] {tool}: NO RESPONSE")
            fail += 1
            continue
        if "error" in r:
            print(f"[X] {tool}: error = {r['error']}")
            fail += 1
            continue
        body = extract_text(r)
        is_error_payload = isinstance(body, dict) and "error" in body
        if is_error_payload:
            print(f"[!] {tool}({args}) → {short(body, 250)}")
            fail += 1
        else:
            print(f"[OK] {tool}({args}) → {short(body, 250)}")
            ok += 1
        print()

    print(f"\n=== Summary ===")
    print(f"  OK:    {ok}")
    print(f"  FAIL:  {fail}")
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
