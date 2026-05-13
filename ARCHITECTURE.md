# Architecture

## Final stack

```mermaid
graph TB
    AGENT["AI Agent (Claude / LM Studio / ...)"]

    subgraph MCP["beckhoff-mcp.exe (.NET 8)"]
        STDIO["JSON-RPC stdio"]
        TOOLS["26 ADS tools<br/>(Tools/*.cs)"]
        MGR["AdsConnectionManager<br/>(per-port AdsSession cache)"]
        DISCO["NetworkDiscovery<br/>(UDP 48899 + TCP scan)"]
        TRACE["TraceService<br/>(notification sessions)"]
        ADSCLIENT["Beckhoff.TwinCAT.Ads<br/>AdsClient + AdsSession"]
        PLUGIN["Beckhoff.TwinCAT.Ads.AdsOverMqtt<br/>(MEF plugin)"]
        MQTTNET["MQTTnet<br/>(actual MQTT transport)"]
    end

    subgraph PLC["PLC / TwinCAT runtime"]
        BROKER["MQTT broker<br/>(Mosquitto, etc.)"]
        TCAT["TwinCAT XAR<br/>(subscribed to topic)"]
    end

    AGENT -->|JSON-RPC| STDIO
    STDIO --> TOOLS
    TOOLS --> MGR
    TOOLS --> DISCO
    TOOLS --> TRACE
    MGR --> ADSCLIENT
    ADSCLIENT --> PLUGIN
    PLUGIN --> MQTTNET
    MQTTNET -->|"publish AdsOverMqtt/{targetNetId}/ams"| BROKER
    BROKER -->|"subscribe AdsOverMqtt/{ourNetId}/ams/#"| MQTTNET
    BROKER --> TCAT
    TCAT --> BROKER

    style PLUGIN fill:#90ee90
    style ADSCLIENT fill:#90ee90
    style MGR fill:#cce5ff
```

Green: the supported Beckhoff path that proves the whole system works without a
local TwinCAT install. Blue: our connection bookkeeping that makes broker /
target switching possible at runtime.

## Discovery and connect at runtime

```mermaid
sequenceDiagram
    participant Agent
    participant Server as beckhoff-mcp
    participant LAN
    participant Broker as MQTT Broker
    participant PLC as TwinCAT PLC

    Agent->>Server: beckhoff_discover_network(subnets / targets)
    Server->>LAN: UDP 48899 ADS discovery (parallel)
    LAN-->>Server: IP, AmsNetId, hostname, TwinCAT version
    Server->>LAN: TCP port probe (22 / 1883 / 8016 / 48898 / ...)
    LAN-->>Server: open ports per host
    Server-->>Agent: list of beckhoff-shaped hosts

    Agent->>Server: beckhoff_discover(broker_host, topic_root)
    Server->>Broker: connect + subscribe '<topic>/+/info'
    Broker-->>Server: peers' online announcements
    Server-->>Agent: NetId / hostname / OS per peer

    Agent->>Server: beckhoff_connect(target_net_id, mqtt_broker, mqtt_topic)
    Server->>Server: ApplyMqttOverride()<br/>rebuild GlobalConfiguration<br/>drop stale AdsSession
    Server->>Broker: AdsClient publish/subscribe via plugin
    Broker->>PLC: AMS frame
    PLC-->>Broker: device info + state
    Broker-->>Server: response
    Server-->>Agent: { runtime: {...}, system_service: {...} }
```

`connect` always probes both the requested target port AND `SystemService` (10000),
so the agent gets a full picture of device-level vs application-level state in one
round-trip.

## Variable read with regex

```mermaid
sequenceDiagram
    participant Agent
    participant Server as beckhoff-mcp
    participant PLC

    Agent->>Server: beckhoff_read_variables(pattern, parent_path)
    Server->>Server: SymbolLoaderFactory.Create(Flat mode)
    Server->>PLC: ADSIGRP_SYM_UPLOAD<br/>(read full symbol list)
    PLC-->>Server: 1217 symbols (one round-trip)
    Server->>Server: client-side regex.IsMatch on InstancePath<br/>(filtered to parent_path prefix)
    loop for each matched symbol
        Server->>PLC: ReadValue(symbol)
        PLC-->>Server: typed value
    end
    Server-->>Agent: { mode: "regex", matched_count, success_count, results: [...] }
```

ADS does **not** offer server-side regex symbol filtering — the entire symbol
table is uploaded once, then filtered locally. The sister-project `tcCLI`
implements this identically. We share the same `SymbolIterator` predicate
pattern from the official Beckhoff library.

## Why this architecture

The repo went through several iterations before landing here. The
`archive/experiments/` folder documents each stop on the way:

| Experiment | What it tried | Why it didn't ship |
|-----------|---------------|--------------------|
| `archive/python-mcp/` | Original pyads-based MCP | Needs `TcAdsDll.dll` + Win32 Window Message IPC to a local TwinCAT System Service. Won't run on a host without TwinCAT installed. |
| `archive/experiments/AdsRouter/` | Standalone .NET TCP-router (Beckhoff.TwinCAT.Ads.TcpRouter) | Connects to a broker and serves TCP-loopback for local clients, but doesn't forward arbitrary NetIds via MQTT. The MEF MQTT-plugin works at the local *Server* endpoint, not as a forwarding adapter. |
| `archive/experiments/AdsBridge/` | Custom TCP↔MQTT bridge in C# | Working proof — implements the AMS frame parser, routes by `<Type>` in StaticRoutes.xml. But Windows `pyads` still can't reach it (TcAdsDll bypasses TCP entirely), so the bridge only helps if you also rewrite the client. |
| `archive/experiments/AdsTestClient/` | Direct .NET AdsClient + AdsOverMqtt plugin | **Worked perfectly.** This is what the final MCP server uses internally — the plugin handles publish/subscribe on the broker, no router needed. The bridge became unnecessary. |

The key insight: **once the client code is .NET, the AdsOverMqtt plugin is the
shortest path** — it's the only Beckhoff-supported way of running an
ADS-over-MQTT-only stack without any local Beckhoff service. Rewriting the MCP
in C# (which we did) removed the need for everything in `archive/experiments/`.

## What couldn't be ported from the sister project

The sister project `Avm.Swiss.TwinCAT.CLI` has many tools that depend on
TwinCAT XAE (the engineering shell) via COM automation: `BuildTools`,
`ProjectTools`, `PouTools`, `LibraryTools`, `RuntimeTools`, `SafetyTools`,
`DialogTools`, `OutputPaneTools`, `SaveTools`, `TaskTools`, `TargetPlatformTools`,
`SessionTools`, `RouteTools`, `ComExplorerTools`. These are excluded by design
— they would re-introduce the XAE dependency we built this server to avoid.

What did get ported: every tool that talks to a running PLC over ADS
(read, write, RPC, type introspection, trace, EtherCAT diagnostics, route
discovery, connect with broker switch).

## Configuration semantics

| Source | Loads | Notes |
|--------|-------|-------|
| `appsettings.json` | Local fallback NetId, default target, MQTT broker config | Read at startup; persisted between runs |
| `connect()` overrides | `mqtt_broker`, `mqtt_port`, `mqtt_topic` | In-memory only; rebuilds `GlobalConfiguration` and drops the stale AdsSession atomically under one lock |
| Auto-generated | `AmsRouter:NetId` if missing | One random `10.x.y.z.1.1` per process |

`connect()` is the single entry point for changing target + broker at runtime.
It applies overrides, sets the intent target (so concurrent calls see the
new target immediately), then opens a fresh `AdsSession` — all under one lock.

## Transports

`beckhoff_connect` exposes three transports, picked via `transport=`:

| Value | Path | When |
|-------|------|------|
| `mqtt` (default) | AdsOverMqtt MEF plugin — frames go via an external MQTT broker, no router process | Default. Works on hosts where TwinCAT is not installed. |
| `tcp` | Starts an in-process `AmsTcpIpRouter` bound to 48898 and routes via TCP/IP. Needs `target_ip` + a backroute on the PLC for our local NetId | Useful when MQTT isn't an option and TwinCAT is not installed locally |
| `local` | No in-process router, no MQTT override — defers to an already-installed TwinCAT router on this machine and uses the routes it already knows | Use on Beckhoff IPCs or engineering workstations where TwinCAT is installed |

`LocalRouterDetector` runs at startup and reports two signals:

- `port_48898_in_use` — something else (typically `TcSysSrv.exe`) already owns 48898 locally
- `tc_sys_srv_running` — the TwinCAT System Service is installed and `Running`

If either is true, `transport='tcp'` is refused (binding 48898 would fail
anyway) and the error response points the agent at `transport='local'`.
The detector signals are also surfaced verbatim in every `beckhoff_connect`
response so the agent can pick the right transport without trial-and-error.

The `local` transport positively sets `AmsRouter:ChannelProtocol=Ads` (so the
embedded default `AdsOverMqtt` does not bleed through) and clears the MQTT
overrides — `Beckhoff.TwinCAT.Ads` then uses its default channel discovery
(PInvoke / UnixSocket / TCP loopback to 48898, whichever the installed router
exposes). The local AmsNetId is read from the registry
(`HKLM\SOFTWARE\Beckhoff\TwinCAT3\System\AmsNetId`, 32-bit view) when
available so the installed router accepts our frames without a manual route
edit.

> **Not yet verified on a real TwinCAT-installed host.** The intent is that
> `transport='local'` lets the managed library reach the installed router
> via whichever IPC channel it auto-discovers; that exact discovery path
> still needs to be confirmed end-to-end. The detector signals
> (`local_router_detected`, `port_48898_in_use`, `tc_sys_srv_running`,
> `installed_net_id`) are always surfaced in the `connect` response so the
> agent can react if `local` fails.
