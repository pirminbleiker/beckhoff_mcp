# Beckhoff MCP Server

An MCP ([Model Context Protocol](https://modelcontextprotocol.io)) server that
gives AI agents direct, read-and-write access to Beckhoff TwinCAT PLCs via ADS
— **without requiring a local TwinCAT installation**.

Communicates with PLCs over **ADS-over-MQTT** (TF6720) using Beckhoff's official
.NET libraries. Brokers and routes can be discovered and added at runtime.

> **Platform:** Windows x64 only. Published as a self-contained .NET 8
> single-file executable (see [Requirements](docs/getting-started.md#requirements)).

## Highlights

- **No TwinCAT install required on the host.** Uses `Beckhoff.TwinCAT.Ads` +
  `Beckhoff.TwinCAT.Ads.AdsOverMqtt` MEF plugin to talk MQTT directly to the PLC's broker.
- **Discovers targets two ways**: MQTT-broker scan (passive — finds peers
  publishing `<topic>/<netId>/info`) and active UDP/TCP network scan
  (UDP 48899 ADS-Discovery + TCP port probe).
- **Dynamic broker switching** at runtime via `beckhoff_connect` —
  no restart needed when reaching different PLC environments.
- **27 ADS tools**: device info/state, symbols (with regex), read/write,
  RPC discovery + invocation, deep type introspection, pointer/reference/interface
  dereferencing, EtherCAT master diagnostics, port-specific queries,
  notification-based variable tracing, and route registration.

## Quick start

```powershell
cd src/BeckhoffMcp.Server
dotnet publish -c Release -o publish
```

Register `publish/beckhoff-mcp.exe` with your MCP client (e.g. Claude
Desktop) and you're connecting. Full walkthrough, including the
pre-built release download and configuration: **[docs/getting-started.md](docs/getting-started.md)**.

## Documentation

| Doc | What's in it |
|---|---|
| [Getting Started](docs/getting-started.md) | Install, configure, register with Claude Desktop, verify |
| [Tools Reference](docs/tools-reference.md) | All 27 tools, grouped by category, plus a typical agent workflow |
| [Configuration](docs/configuration.md) | Config layering/override precedence, the three connection transports |
| [Architecture](docs/architecture.md) | How the server talks to the PLC, and why it ended up on this design |
| [Troubleshooting](docs/troubleshooting.md) | Error codes, timeouts, symbol-table quirks |

## Why .NET (and not pyads)?

`pyads` on Windows uses `TcAdsDll.dll`, which talks to the local TwinCAT
System Service via Win32 Window Message IPC — a service that doesn't exist
without TwinCAT installed.

`Beckhoff.TwinCAT.Ads` (the official .NET client library) plus the
`AdsOverMqtt` MEF plugin sidesteps the local-service requirement entirely —
the .NET client publishes AMS frames directly to a configurable MQTT broker
and the PLC receives them through its own MQTT subscription. No router
service needed; no TwinCAT install needed; no firewall holes for port 48898.

The trade-off: client code is C#, not Python. [docs/architecture.md](docs/architecture.md#why-this-architecture)
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
