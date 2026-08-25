# Configuration

The exe ships with an embedded default `appsettings.json` (see
[`src/BeckhoffMcp.Server/appsettings.json`](../src/BeckhoffMcp.Server/appsettings.json))
and runs out of the box without any external file.

## Override precedence

Config sources are layered, later wins:

1. embedded default (baked into the exe)
2. `appsettings.json` next to the exe (optional)
3. file pointed at by the `BECKHOFF_MCP_APPSETTINGS` env var (optional)
4. `BECKHOFF_MCP_*` env vars (e.g. `BECKHOFF_MCP_AmsRouter__NetId`)
5. command line args (e.g. `--AmsRouter:NetId=...`)

## Example `appsettings.json`

```json
{
  "AmsRouter": {
    "Name": "BeckhoffMcp",
    "NetId": "10.42.17.203.1.1",
    "ChannelProtocol": "AdsOverMqtt",
    "Mqtt": [
      {
        "Address": "192.0.2.10",
        "Port": 1883,
        "Topic": "AdsOverMqtt",
        "NoRetain": false,
        "Unidirectional": false
      }
    ]
  }
}
```

(`192.0.2.0/24` above is the [RFC 5737](https://www.rfc-editor.org/rfc/rfc5737)
documentation range — substitute your actual broker address.)

## Configuration semantics

| Source | Loads | Notes |
|--------|-------|-------|
| `appsettings.json` | Local fallback NetId, default target, MQTT broker config | Read at startup; persisted between runs |
| `connect()` overrides | `mqtt_broker`, `mqtt_port`, `mqtt_topic` | In-memory only; rebuilds `GlobalConfiguration` and drops the stale AdsSession atomically under one lock |
| Auto-generated | `AmsRouter:NetId` if left unset | One random `10.x.y.z.1.1` per process, then persisted back to the on-disk `appsettings.json` |

`AmsRouter:NetId` is auto-generated on first launch **only if it's not set
anywhere in the layered config above** — the program writes the generated
value into the on-disk `appsettings.json` next to the exe so the same
identity persists across restarts (important: a backroute on the PLC stays
valid). Every tool can override the active target at runtime via
`beckhoff_connect` regardless of what's configured on disk.

`connect()` is the single entry point for changing target + broker at
runtime. It applies overrides, sets the intent target (so concurrent calls
see the new target immediately), then opens a fresh `AdsSession` — all
under one lock.

## Transports

`beckhoff_connect` exposes three transports, picked via `transport=`:

| Value | Path | When |
|-------|------|------|
| `mqtt` (default) | AdsOverMqtt MEF plugin — frames go via an external MQTT broker, no router process | Default. Works on hosts where TwinCAT is not installed. |
| `tcp` | Starts an in-process `AmsTcpIpRouter` bound to 48898 and routes via TCP/IP. Needs `target_ip` + a backroute on the PLC for our local NetId (see [`beckhoff_add_route`](tools-reference.md#beckhoff_add_route--registering-a-backroute-windows-only)) | Useful when MQTT isn't an option and TwinCAT is not installed locally |
| `local` | No in-process router, no MQTT override — defers to an already-installed TwinCAT router on this machine and uses the routes it already knows | Use on Beckhoff IPCs or engineering workstations where TwinCAT is installed |

`LocalRouterDetector` runs at startup and reports two signals:

- `port_48898_in_use` — something else (typically `TcSysSrv.exe`) already owns 48898 locally
- `tc_sys_srv_running` — the TwinCAT System Service is installed and `Running`

If either is true, `transport='tcp'` is refused (binding 48898 would fail
anyway) and the error response points the agent at `transport='local'`.
The detector signals are also surfaced verbatim in every `beckhoff_connect`
response so the agent can pick the right transport without trial-and-error.

The `local` transport positively sets `AmsRouter:ChannelProtocol=Ads` (so
the embedded default `AdsOverMqtt` does not bleed through) and clears the
MQTT overrides — `Beckhoff.TwinCAT.Ads` then uses its default channel
discovery (PInvoke / UnixSocket / TCP loopback to 48898, whichever the
installed router exposes). The local AmsNetId is read from the registry
(`HKLM\SOFTWARE\Beckhoff\TwinCAT3\System\AmsNetId`, 32-bit view) when
available so the installed router accepts our frames without a manual
route edit.

> **Not yet verified on a real TwinCAT-installed host.** The intent is that
> `transport='local'` lets the managed library reach the installed router
> via whichever IPC channel it auto-discovers; that exact discovery path
> still needs to be confirmed end-to-end. The detector signals
> (`local_router_detected`, `port_48898_in_use`, `tc_sys_srv_running`,
> `installed_net_id`) are always surfaced in the `connect` response so the
> agent can react if `local` fails.
