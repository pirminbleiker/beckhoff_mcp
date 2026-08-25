# Beckhoff MCP Server

An MCP ([Model Context Protocol](https://modelcontextprotocol.io)) server that
gives AI agents direct, read-and-write access to Beckhoff TwinCAT PLCs via ADS
— **without requiring a local TwinCAT installation**.

Communicates with PLCs over **ADS-over-MQTT** (TF6720) using Beckhoff's official
.NET libraries. Brokers and routes can be discovered and added at runtime.

## Highlights

- **No TwinCAT install required on the host.** Uses `Beckhoff.TwinCAT.Ads` +
  `Beckhoff.TwinCAT.Ads.AdsOverMqtt` MEF plugin to talk MQTT directly to the PLC's broker.
- **Discovers targets two ways**: MQTT-broker scan (passive — finds peers
  publishing `<topic>/<netId>/info`) and active UDP/TCP network scan
  (UDP 48899 ADS-Discovery + TCP port probe).
- **Dynamic broker switching** at runtime via `beckhoff_connect` —
  no restart needed when reaching different PLC environments.
- **26 ADS tools**: device info/state, symbols (with regex), read/write,
  RPC discovery + invocation, deep type introspection, pointer/reference/interface
  dereferencing, EtherCAT master diagnostics, port-specific queries, and
  notification-based variable tracing.

## Quick start

### 1. Get the binary

**Option A — download a release (recommended):**

Grab `beckhoff-mcp-<version>-win-x64.exe` from the [Releases page](../../releases)
and put it anywhere (e.g. `C:\Tools\beckhoff-mcp.exe`). It is a single
self-contained .NET 8 file — no install, no TwinCAT prerequisite, no
companion DLLs to copy.

**Option B — build from source:**

```powershell
cd src/BeckhoffMcp.Server
dotnet publish -c Release -o publish
```

Output: `src/BeckhoffMcp.Server/publish/beckhoff-mcp.exe` (single-file, self-contained).

### 2. Configure (optional)

The exe ships with an embedded default `appsettings.json` and runs out of
the box. To override anything persistently, drop a real `appsettings.json`
next to the exe — the program merges it on top of the embedded defaults.

Example `appsettings.json`:

```json
{
  "Beckhoff": {
    "TargetNetId": "169.254.34.222.1.1",
    "TargetPort": 851
  },
  "AmsRouter": {
    "Name": "BeckhoffMcp",
    "NetId": "192.168.71.5.1.1",
    "ChannelProtocol": "AdsOverMqtt",
    "Mqtt": [
      {
        "Address": "192.168.71.38",
        "Port": 1883,
        "Topic": "AdsOverMqtt",
        "NoRetain": false,
        "Unidirectional": false
      }
    ]
  }
}
```

`AmsRouter:NetId` is auto-generated on first launch if missing — the
program writes the generated value into the on-disk `appsettings.json` so
the same identity persists across restarts (important: a backroute on the
PLC stays valid). `Beckhoff:TargetNetId` is the default target; every tool
can override at runtime via `beckhoff_connect`.

Override file lookup order (later wins):

1. embedded default (in the exe)
2. `appsettings.json` next to the exe
3. file pointed at by `BECKHOFF_MCP_APPSETTINGS` env var
4. `BECKHOFF_MCP_*` env vars
5. command line args (e.g. `--Beckhoff:TargetNetId=...`)

### 3. Register with Claude Desktop

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

### 4. Verify

No bundled test script — verify manually via an MCP client (e.g. Claude
Desktop): call `beckhoff_discover_network` / `beckhoff_connect`, then a
`beckhoff_list_symbols` / `beckhoff_read_variables` round-trip against a
reachable target.

## Tool surface

| Category | Tools |
|----------|-------|
| Discovery | `beckhoff_discover` (MQTT broker scan), `beckhoff_discover_network` (UDP 48899 + TCP scan) |
| Connection | `beckhoff_connect` (`transport=mqtt`/`tcp`/`local`, with broker/topic override), `beckhoff_connection_status` |
| Device | `beckhoff_get_device_info`, `beckhoff_get_device_state` (both accept `port` override) |
| Symbols | `beckhoff_list_symbols` (substring + regex + `parent_path` scope), `beckhoff_get_symbol_info` |
| Read | `beckhoff_read_variable`, `beckhoff_read_variables` (explicit list **or** regex pattern) |
| Write | `beckhoff_write_variable`, `beckhoff_write_variables`, `beckhoff_write_control` |
| Type introspection | `beckhoff_get_type_info` (members, RPC methods, base type, interfaces, refs), `beckhoff_dereference` |
| RPC | `beckhoff_get_rpc_methods`, `beckhoff_invoke_rpc` |
| Trace | `beckhoff_trace_start`, `beckhoff_trace_get` (events / summary / csv), `beckhoff_trace_stop` |
| Port-specific | `beckhoff_query_ads_port`, `beckhoff_read_from_port` |
| EtherCAT | `beckhoff_get_ethercat_master_state`, `beckhoff_get_ethercat_slave_count`, `beckhoff_get_ethercat_topology`, `beckhoff_get_ethercat_slave_info` |

## Typical agent workflow

```text
1. beckhoff_discover_network                  → find PLCs on the LAN
   ↳ returns IPs, AmsNetIds, hostnames, TwinCAT versions

2. beckhoff_discover (MQTT)                   → find peers on a known broker
   ↳ returns NetIds + online status

3. beckhoff_connect(target_net_id,            → set active target + broker
                    mqtt_broker, mqtt_topic)
   ↳ returns runtime AND system_service status simultaneously
   ↳ also returns local_router_detected — if true, retry with
     transport='local' to use the installed TwinCAT router's routes

4. beckhoff_list_symbols(pattern=...)         → discover variables
5. beckhoff_read_variables(pattern=...)       → bulk read by regex
6. beckhoff_invoke_rpc(...)                   → call PLC methods
7. beckhoff_trace_start / trace_get           → record value-change history
```

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for the detailed picture, including
why the project ended up using `Beckhoff.TwinCAT.Ads.AdsOverMqtt` directly
instead of writing a custom router or bridge.

## Project layout

```
twincat-ads-mcp/
├── src/
│   └── BeckhoffMcp.Server/         .NET 8 MCP server (the product)
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Services/
│       │   ├── AdsConnectionManager.cs   AdsSession lifecycle + MQTT-override
│       │   ├── NetworkDiscovery.cs       UDP 48899 + TCP port scanner
│       │   └── TraceService.cs           ADS-notification subscription pool
│       └── Tools/
│           ├── DeviceTools.cs            Device info/state (with port-override)
│           ├── SymbolTools.cs            list/read symbols (regex)
│           ├── WriteTools.cs             write_variable(s) / write_control
│           ├── TypeTools.cs              get_type_info / dereference
│           ├── RpcTools.cs               get_rpc_methods / invoke_rpc
│           ├── TraceTools.cs             trace_start / get / stop
│           ├── DiscoveryTools.cs         discover (MQTT) + connect
│           ├── EthercatTools.cs          EtherCAT master diagnostics
│           └── PortTools.cs              query_ads_port / read_from_port
└── ARCHITECTURE.md                    Architecture & design history (incl. earlier experiments, since removed — see git history)
```

## Why .NET (and not pyads)?

`pyads` on Windows uses `TcAdsDll.dll`, which talks to the local TwinCAT
System Service via Win32 Window Message IPC — a service that doesn't exist
without TwinCAT installed.

`Beckhoff.TwinCAT.Ads` (the official .NET client library) plus the
`AdsOverMqtt` MEF plugin sidesteps the local-service requirement entirely —
the .NET client publishes AMS frames directly to a configurable MQTT broker
and the PLC receives them through its own MQTT subscription. No router
service needed; no TwinCAT install needed; no firewall holes for port 48898.

The trade-off: client code is C#, not Python. [ARCHITECTURE.md](ARCHITECTURE.md#why-this-architecture)
documents the experiments (custom TCP router, custom TCP↔MQTT bridge, drop-in
DLL replacement) that explored other paths before settling on the .NET
direct-broker route; the experimental code itself has since been removed
from the repo (see git history).

## Releases

Releases are produced by `.github/workflows/release.yml`:

- Push a tag matching `v*` (e.g. `git tag v0.1.0 && git push origin v0.1.0`),
  or trigger the workflow manually from the **Actions** tab and supply a tag.
- The workflow runs on `windows-latest`, runs `dotnet publish -c Release`
  (which produces a single self-contained exe via the csproj's
  `PublishSingleFile` + `IncludeAllContentForSelfExtract` settings), and
  attaches `beckhoff-mcp-<tag>-win-x64.exe` plus a `.sha256` to the release.

## License

See [LICENSE](LICENSE).
