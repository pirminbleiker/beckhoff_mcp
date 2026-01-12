# Beckhoff PLC MCP Server

An MCP (Model Context Protocol) server that provides read-only access to Beckhoff TwinCAT PLCs via ADS (Automation Device Specification).

## Features

- Query PLC device information and state
- List and search PLC symbols
- Read single or multiple PLC variables
- **Dynamic PLC discovery and connection** via UDP (port 48899)
- **EtherCAT diagnostics** - master state, slave count, topology, and slave info
- **Custom ADS port access** - query and read from any ADS port (e.g., I/O Image ports)
- **Graceful startup** - server starts even if no local PLC is connected
- Automatic ADS route creation
- Automatic symbol caching for fast lookups
- Support for common TwinCAT data types

## Prerequisites

- Python 3.10 or higher
- Beckhoff TwinCAT runtime with ADS enabled
- ADS routing configured (for remote PLCs)

## Installation

```bash
# Clone or navigate to the project directory
cd beckhoff_mcp

# Install with pip
pip install -e .

# Or install dependencies directly
pip install mcp pyads pydantic
```

## Uninstallation

To uninstall the current version before installing a new one:

```bash
# Uninstall the package
pip uninstall beckhoff_mcp

# Verify it's removed
pip show beckhoff_mcp
```

## Configuration

### Default Connection Settings

The server attempts to connect to a local TwinCAT PLC on startup:

```python
AMS_NET_ID = "127.0.0.1.1.1"  # Localhost AMS Net ID
AMS_PORT = 851                 # TwinCAT 3 PLC Runtime 1
```

**Note:** If no local PLC is available (e.g., TwinCAT is not running or is in Config mode), the server will start in **disconnected mode**. You can then use `beckhoff_discover_and_connect` to connect to a remote PLC.

To modify the default settings, edit `src/beckhoff_mcp/server.py` and update the constants at the top of the file.

### Dynamic Connection (Runtime)

You can also connect to remote PLCs at runtime using the `beckhoff_discover_and_connect` tool. Simply ask Claude to connect to a specific IP address:

```
<beckhoff:mcp: ip=192.168.1.54>
```

Or with credentials for automatic route creation:

```
Connect to PLC at 192.168.1.54 with username admin and password secret
```

### Common AMS Port Numbers

| Port | Description |
|------|-------------|
| 851  | TwinCAT 3 PLC Runtime 1 |
| 852  | TwinCAT 3 PLC Runtime 2 |
| 853  | TwinCAT 3 PLC Runtime 3 |
| 854  | TwinCAT 3 PLC Runtime 4 |

## Usage

### Running the Server

```bash
# Using the installed script
beckhoff_mcp

# Or run directly
python -m beckhoff_mcp.server
```

### Claude Desktop Configuration

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "beckhoff_mcp": {
      "command": "python",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

Or if installed as a package:

```json
{
  "mcpServers": {
    "beckhoff_mcp": {
      "command": "beckhoff_mcp"
    }
  }
}
```

## Available Tools

### `beckhoff_get_device_info`

Get PLC device name and version information.

**Input:** None

**Output:**
```json
{
  "device_name": "TwinCAT PLC",
  "version": {
    "major": 3,
    "minor": 1,
    "build": 4024
  }
}
```

### `beckhoff_get_device_state`

Get current ADS state and device state.

**Input:** None

**Output:**
```json
{
  "ads_state": {
    "code": 5,
    "name": "Run"
  },
  "device_state": 0
}
```

### `beckhoff_list_symbols`

List available PLC symbols with optional filtering and pagination.

**Input:**
- `filter` (optional): Filter pattern with wildcards (e.g., `"GVL_*"`, `"*Motor*"`)
- `limit` (optional, default 50): Maximum results to return
- `offset` (optional, default 0): Pagination offset

**Output:**
```json
{
  "symbols": [
    {"name": "GVL.bStart", "type": "BOOL", "comment": "Start button"},
    {"name": "GVL.nCounter", "type": "INT", "comment": "Cycle counter"}
  ],
  "pagination": {
    "total": 150,
    "offset": 0,
    "limit": 50,
    "returned": 50,
    "has_more": true,
    "next_offset": 50
  }
}
```

### `beckhoff_get_symbol_info`

Get detailed information about a specific symbol.

**Input:**
- `symbol_name` (required): Full symbol name (e.g., `"MAIN.bStart"`)

**Output:**
```json
{
  "name": "MAIN.bStart",
  "type": "BOOL",
  "index_group": 16448,
  "index_offset": 0,
  "size": 1,
  "comment": "Start button"
}
```

### `beckhoff_read_variable`

Read a single PLC variable by symbol name.

**Input:**
- `symbol_name` (required): Full variable name (e.g., `"GVL.nCounter"`)

**Output:**
```json
{
  "name": "GVL.nCounter",
  "type": "INT",
  "value": 42
}
```

### `beckhoff_read_variables`

Read multiple PLC variables at once.

**Input:**
- `symbol_names` (required): List of variable names

**Output:**
```json
{
  "results": [
    {"name": "GVL.bStart", "type": "BOOL", "value": true},
    {"name": "GVL.nCounter", "type": "INT", "value": 42}
  ],
  "errors": null,
  "summary": {
    "requested": 2,
    "successful": 2,
    "failed": 0
  }
}
```

### `beckhoff_discover_and_connect`

Discover and connect to a remote PLC by IP address using UDP discovery.

**Input:**
- `ip_address` (required): IP address of the target PLC
- `username` (optional): PLC login for route creation
- `password` (optional): PLC password for route creation
- `ams_port` (optional, default 851): ADS port number
- `route_name` (optional): Custom name for the route

**Output:**
```json
{
  "success": true,
  "ip_address": "192.168.1.54",
  "discovered_ams_net_id": "192.168.1.54.1.1",
  "ams_port": 851,
  "route_added": true,
  "device_info": {
    "device_name": "CX-12345",
    "version": {"version": 3, "revision": 1, "build": 4024}
  },
  "symbols_cached": 150
}
```

### `beckhoff_connection_status`

Get information about the current PLC connection.

**Input:** None

**Output:**
```json
{
  "ams_net_id": "192.168.1.54.1.1",
  "ams_port": 851,
  "ip_address": "192.168.1.54",
  "is_remote": true,
  "is_connected": true,
  "symbols_cached": 150
}
```

## EtherCAT Diagnostic Tools

### `beckhoff_get_ethercat_master_state`

Get the EtherCAT master state (Init, PreOp, SafeOp, Op).

**Input:** None

**Output:**
```json
{
  "master_state": {"code": 8, "name": "Op"},
  "ams_net_id": "192.168.1.54.1.1",
  "ethercat_port": 65535
}
```

### `beckhoff_get_ethercat_slave_count`

Get the number of connected EtherCAT slaves.

**Input:** None

**Output:**
```json
{
  "slave_count": 5,
  "ams_net_id": "192.168.1.54.1.1"
}
```

### `beckhoff_get_ethercat_topology`

Get the full EtherCAT network topology with all slave information.

**Input:** None

**Output:**
```json
{
  "slave_count": 3,
  "slaves": [
    {"address": 1, "state": {"code": 8, "name": "Op"}, "vendor_id": 2, "product_code": 100663346},
    {"address": 2, "state": {"code": 8, "name": "Op"}, "vendor_id": 2, "product_code": 100728882}
  ],
  "ams_net_id": "192.168.1.54.1.1"
}
```

### `beckhoff_get_ethercat_slave_info`

Get detailed information about a specific EtherCAT slave.

**Input:**
- `slave_address` (required): The slave address (1-indexed)

**Output:**
```json
{
  "address": 1,
  "state": {"code": 8, "name": "Op"},
  "vendor_id": 2,
  "product_code": 100663346,
  "crc_errors": 0,
  "ams_net_id": "192.168.1.54.1.1"
}
```

## Custom ADS Port Tools

Some EtherCAT devices (like EK1200 EtherCAT Couplers) expose their I/O data on custom ADS ports rather than the standard EtherCAT master port (0xFFFF). Use these tools to access such devices.

### `beckhoff_query_ads_port`

Query a custom ADS port to discover available symbols and device information.

**Input:**
- `ads_port` (required): The ADS port number (e.g., 27905 or 0x6D01)

**Output:**
```json
{
  "ads_port": 27905,
  "ads_port_hex": "0x6D01",
  "ams_net_id": "5.59.203.226.1.1",
  "ip_address": "192.168.1.67",
  "success": true,
  "device_info": {
    "device_name": "EK1200",
    "version": {"version": 3, "revision": 1, "build": 4024}
  },
  "state": {
    "ads_state": {"code": 5, "name": "Run"},
    "device_state": 0
  },
  "symbol_count": 4,
  "symbols_preview": [
    {"name": "Inputs.EL1008.Channel 1.Input", "type": "BOOL"},
    {"name": "Outputs.EL2008.Channel 1.Output", "type": "BOOL"}
  ]
}
```

### `beckhoff_read_from_port`

Read a variable from a custom ADS port.

**Input:**
- `ads_port` (required): The ADS port number (e.g., 27905)
- `symbol_name` (required): The symbol name to read

**Output:**
```json
{
  "name": "Inputs.EL1008.Channel 1.Input",
  "type": "BOOL",
  "value": true,
  "ads_port": 27905
}
```

**How to find the ADS port:** In TwinCAT XAE, double-click on the device (e.g., under I/O > Devices > EtherCAT), go to the "ADS" tab, and note the "ADS Server Port" value.

## Supported Data Types

| TwinCAT Type | Description |
|--------------|-------------|
| BOOL | Boolean (TRUE/FALSE) |
| BYTE | 8-bit unsigned |
| SINT | 8-bit signed |
| USINT | 8-bit unsigned |
| INT | 16-bit signed |
| UINT | 16-bit unsigned |
| DINT | 32-bit signed |
| UDINT | 32-bit unsigned |
| LINT | 64-bit signed |
| ULINT | 64-bit unsigned |
| REAL | 32-bit float |
| LREAL | 64-bit float |
| STRING | Text string |
| TIME, DATE, DT, TOD | Time types (as UDINT) |
| WORD, DWORD, LWORD | Bit string types |

**Note:** Complex types (arrays, structures) are not fully supported for reading. The server will return type information but may not be able to read the value directly.

## Troubleshooting

### Connection Errors

If you see connection errors:

1. **Verify TwinCAT is running**: Check that TwinCAT System Service is running and the PLC is in Run mode
2. **Check AMS Net ID**: Ensure the AMS Net ID in the server matches your system (run `hostname` in TwinCAT XAE to find it)
3. **Verify ADS routing**: For remote connections, ensure ADS routes are configured on both systems
4. **Check firewall**: ADS uses TCP port 48898 by default

### Symbol Not Found

If symbols are not found:

1. Ensure the PLC project is activated and running
2. Check the exact symbol name including namespace (e.g., `MAIN.`, `GVL.`)
3. Symbol names are case-sensitive
4. Try `beckhoff_list_symbols` with a filter to find the correct name

## License

MIT License
