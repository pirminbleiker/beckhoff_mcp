# Tools Reference

27 tools, all prefixed `beckhoff_`.

| Category | Tools |
|----------|-------|
| Discovery | `beckhoff_discover` (MQTT broker scan), `beckhoff_discover_network` (UDP 48899 + TCP scan) |
| Connection | `beckhoff_connect` (`transport=mqtt`/`tcp`/`local`, with broker/topic override), `beckhoff_connection_status` |
| Route management | `beckhoff_add_route` (registers our local AmsNetId as a route on the target PLC — see below) |
| Device | `beckhoff_get_device_info`, `beckhoff_get_device_state` (both accept `port` override) |
| Symbols | `beckhoff_list_symbols` (substring + regex + `parent_path` scope), `beckhoff_get_symbol_info` |
| Read | `beckhoff_read_variable`, `beckhoff_read_variables` (explicit list **or** regex pattern) |
| Write | `beckhoff_write_variable`, `beckhoff_write_variables`, `beckhoff_write_control` |
| Type introspection | `beckhoff_get_type_info` (members, RPC methods, base type, interfaces, refs), `beckhoff_dereference` |
| RPC | `beckhoff_get_rpc_methods`, `beckhoff_invoke_rpc` |
| Trace | `beckhoff_trace_start`, `beckhoff_trace_get` (events / summary / csv), `beckhoff_trace_stop` |
| Port-specific | `beckhoff_query_ads_port`, `beckhoff_read_from_port` |
| EtherCAT | `beckhoff_get_ethercat_master_state`, `beckhoff_get_ethercat_slave_count`, `beckhoff_get_ethercat_topology`, `beckhoff_get_ethercat_slave_info` |

## `beckhoff_add_route` — registering a backroute (Windows only)

`transport=tcp` (and some `local` setups) need the PLC to know a route back
to this machine's AmsNetId — otherwise it silently drops AMS frames.
`beckhoff_add_route` registers that route over UDP/48899, the same wire
format TwinCAT XAE's "Add Route Dialog" uses.

What it does, in order:

1. Checks the target answers on UDP/48899 (fails fast with a hint to run
   `beckhoff_discover_network` first if not).
2. Best-effort checks whether the route already exists (no-ops if so —
   `AddRoute` itself is idempotent even when this check can't run without
   auth on newer TwinCAT versions).
3. If credentials are needed: tries a saved credential first (Windows
   Credential Manager, keyed by the target IP), otherwise **pops the
   standard Windows credential dialog** (the same UI RDP/SMB use). Nothing
   is ever written in plaintext to the MCP's own config or logs — the
   optional "save" checkbox stores it DPAPI-encrypted in the per-user
   Windows Credential Manager, not this project's `appsettings.json`.
4. Registers the route, defaulting to **temporary** (gone on PLC reboot) so
   the MCP doesn't permanently modify `StaticRoutes.xml` on the target
   unless you explicitly ask for `temporary=false`.

Because it opens a native OS dialog, `beckhoff_add_route` expects to run
somewhere with an interactive Windows desktop session — it will not work
headlessly if no saved credential exists yet and `dry_run` isn't set.

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
   ↳ if transport='tcp' fails with "no route" / AMS frames get dropped,
     call beckhoff_add_route first, then retry connect

4. beckhoff_list_symbols(pattern=...)         → discover variables
5. beckhoff_read_variables(pattern=...)       → bulk read by regex
6. beckhoff_invoke_rpc(...)                   → call PLC methods
7. beckhoff_trace_start / trace_get           → record value-change history
```

See [troubleshooting.md](troubleshooting.md) for the operational quirks
behind several of these tools (cold-start symbol load, timeouts, pointer
members, error codes).
