# Getting Started

## Requirements

- **Windows x64.** The server is published as a Windows-only, self-contained
  .NET 8 binary (route registration and saved-credential handling use native
  Windows APIs — Credential Manager / CredUI). It does not run on Linux or
  macOS.
- **No local TwinCAT installation required** for the default (`mqtt`)
  transport — see [configuration.md](configuration.md#transports) for when
  you'd want one anyway.

## 1. Get the binary

**Option A — download a release (recommended):**

Grab `beckhoff-mcp-<version>-win-x64.exe` from the [Releases page](../../../releases)
and put it anywhere (e.g. `C:\Tools\beckhoff-mcp.exe`). It is a single
self-contained .NET 8 file — no install, no TwinCAT prerequisite, no
companion DLLs to copy.

**Option B — build from source:**

```powershell
cd src/BeckhoffMcp.Server
dotnet publish -c Release -o publish
```

Output: `src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe` (single-file,
self-contained — verified against the current `.csproj` settings).

## 2. Configure (optional)

The exe ships with an embedded default `appsettings.json` and runs out of
the box. To override anything persistently, drop a real `appsettings.json`
next to the exe — the program merges it on top of the embedded defaults.

See [configuration.md](configuration.md) for the full reference (override
precedence, transports, all config keys). Minimal example:

```json
{
  "AmsRouter": {
    "NetId": "10.42.17.203.1.1"
  }
}
```

`AmsRouter:NetId` is auto-generated on first launch if left unset — the
program writes the generated value into the on-disk `appsettings.json` so
the same identity persists across restarts (important: a backroute on the
PLC stays valid). Every tool can override the target at runtime via
`beckhoff_connect`.

## 3. Register with Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "C:\\Tools\\beckhoff-mcp\\beckhoff-mcp.exe"
    }
  }
}
```

The path must point at the extracted `beckhoff-mcp.exe`. The MCP picks up
`appsettings.json` from the directory next to the exe.

## 4. Verify

There is no bundled test script — verify manually via an MCP client (e.g.
Claude Desktop):

1. `beckhoff_discover_network` — should list PLCs reachable on the LAN.
2. `beckhoff_connect(target_net_id=...)` — connect to one of them.
3. `beckhoff_list_symbols` — should return a non-empty symbol table (if it
   returns 0 on the very first call after connect, call it once more — see
   [troubleshooting.md](troubleshooting.md#cold-start-symbol-load)).
4. `beckhoff_read_variables(pattern=...)` — read a few values back.

## Next steps

- [Tools reference](tools-reference.md) — full tool list and a typical
  end-to-end agent workflow.
- [Configuration](configuration.md) — every config key, override
  precedence, and the three connection transports.
- [Troubleshooting](troubleshooting.md) — common error codes and
  operational quirks (cold-start, timeouts, pointer members, ...).
- [Architecture](architecture.md) — how the server talks to the PLC and why
  it ended up on this design.
