"""Smoke-test: launch the MCP from a cwd that has no appsettings.json.

This simulates Claude Desktop, which usually starts the exe from %USERPROFILE%
or the user's profile dir — never the install dir. Before the cwd fix the MCP
crashed because it looked for appsettings.json in the cwd.
"""
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import os
EXE = Path(os.environ.get("BECKHOFF_MCP_EXE",
    r"D:/Projects/Open Source/beckhoff_mcp/src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe")).resolve()
print(f"EXE = {EXE}")

with tempfile.TemporaryDirectory() as tmp:
    print(f"Launching MCP from cwd={tmp} (no appsettings.json present here).")
    proc = subprocess.Popen(
        [str(EXE)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", cwd=tmp, bufsize=1,
    )
    try:
        proc.stdin.write(json.dumps({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                       "clientInfo": {"name": "cwd-test", "version": "0.1"}}
        }) + "\n")
        proc.stdin.flush()
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()

        deadline = time.time() + 10.0
        first_response = None
        while time.time() < deadline:
            line = proc.stdout.readline()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if obj.get("id") == 1:
                first_response = obj
                break

        if first_response and "result" in first_response:
            print("OK — MCP responded to initialize from foreign cwd:")
            print(f"  serverInfo: {first_response['result'].get('serverInfo')}")
            sys.exit(0)
        else:
            print("FAIL — no valid initialize response. Stderr tail:")
            try:
                proc.stdin.close()
                proc.wait(timeout=2)
            except Exception:
                proc.kill()
            print((proc.stderr.read() or "")[:2000])
            sys.exit(1)
    finally:
        try: proc.stdin.close()
        except Exception: pass
        try: proc.wait(timeout=2)
        except Exception: proc.kill()
