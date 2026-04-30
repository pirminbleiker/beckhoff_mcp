# Beckhoff PLC MCP Server

## What is this?

The Beckhoff PLC MCP Server is a **Model Context Protocol (MCP)** server that enables Claude (or other MCP-compatible AI assistants) to communicate directly with Beckhoff TwinCAT PLCs. It provides read-only access to PLC data, allowing you to query device information, browse symbols, and read variable values through natural language conversations.

## What is MCP?

**Model Context Protocol (MCP)** is an open standard developed by Anthropic that allows AI assistants to securely connect to external data sources and tools. Think of it as a "plugin system" for AI assistants that enables them to:

- Access real-time data from external systems
- Execute specific operations through defined tools
- Maintain context across conversations

MCP servers run locally on your machine and communicate with Claude Desktop via stdio (standard input/output), ensuring your data stays local and secure.

## How It Works

```
┌─────────────────┐     stdio      ┌─────────────────┐      ADS       ┌─────────────┐
│  Claude Desktop │ ◄────────────► │  beckhoff_mcp   │ ◄────────────► │  TwinCAT    │
│  (MCP Client)   │   JSON-RPC     │  (MCP Server)   │   TCP/IP       │  PLC        │
└─────────────────┘                └─────────────────┘                └─────────────┘
```

1. **Claude Desktop** sends tool requests to the MCP server via JSON-RPC over stdio
2. **beckhoff_mcp** translates these requests into ADS (Automation Device Specification) commands
3. **pyads** library communicates with the TwinCAT runtime over TCP/IP
4. Results are returned back through the chain to Claude

### Architecture

- **Transport**: stdio (standard input/output)
- **Protocol**: JSON-RPC 2.0 (MCP standard)
- **PLC Communication**: ADS protocol via pyads library
- **Discovery**: UDP port 48899 for PLC discovery
- **Operations**: Read-only (no writing to PLC variables for safety)
- **Startup**: Graceful - starts even without a local PLC connection

## Installation Prerequisites

### 1. Python 3.10 or higher

Download from [python.org](https://www.python.org/downloads/) or use your preferred Python distribution.

Verify installation:
```bash
python --version
```

### 2. Beckhoff TwinCAT

You need TwinCAT installed on the machine where the MCP server runs. This provides:
- The ADS communication layer
- The `TcAdsDll.dll` library required by pyads

TwinCAT can be:
- **TwinCAT XAE** (Engineering environment) - includes everything
- **TwinCAT XAR** (Runtime only) - sufficient if just connecting to PLCs
- **TwinCAT ADS** (Communication library only) - minimal installation

Download from [Beckhoff website](https://www.beckhoff.com/en-en/products/automation/twincat/) (requires registration).

### 3. ADS Route Configuration

For remote PLCs, you need to configure ADS routes:
1. Open TwinCAT XAE or the ADS Router
2. Add a route to your target PLC
3. Note the AMS Net ID (e.g., `192.168.1.100.1.1`)

For local development with a simulated PLC, the default `127.0.0.1.1.1` works.

### 4. Claude Desktop

Download from [claude.ai](https://claude.ai/download) - the MCP feature is available in the desktop application.

## Installation

### Option 1: Install from source (recommended)

```bash
# Navigate to the project directory
cd path/to/beckhoff_mcp

# Install in editable mode (allows you to modify the code)
pip install -e .
```

### Option 2: Install dependencies manually

```bash
pip install mcp pyads pydantic
```

Then run directly:
```bash
python -m beckhoff_mcp.server
```

## Uninstallation

To uninstall before upgrading to a new version:

```bash
# Uninstall the package
pip uninstall beckhoff_mcp

# Verify it's removed
pip show beckhoff_mcp
# Should output: WARNING: Package(s) not found: beckhoff_mcp
```

## Configuration

### 1. Configure the MCP Server

Edit `src/beckhoff_mcp/server.py` to set your PLC connection details:

```python
# Default connection settings (near the top of the file)
AMS_NET_ID = "127.0.0.1.1.1"  # Change to your PLC's AMS Net ID
AMS_PORT = 851                 # TwinCAT 3 PLC Runtime 1 (851-854)
```

**Disconnected Mode**: If no local PLC is available on startup (e.g., TwinCAT is not running or is in Config mode), the server will start in **disconnected mode**. You can then use `beckhoff_discover_and_connect` to connect to a remote PLC. All tools will return helpful error messages indicating you need to connect first.

Common port numbers:
| Port | Description |
|------|-------------|
| 851  | TwinCAT 3 PLC Runtime 1 |
| 852  | TwinCAT 3 PLC Runtime 2 |
| 853  | TwinCAT 3 PLC Runtime 3 |
| 854  | TwinCAT 3 PLC Runtime 4 |

### 2. Configure Claude Desktop

Create or edit `%APPDATA%\Claude\claude_desktop_config.json`:

**Windows path**: `C:\Users\<username>\AppData\Roaming\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "beckhoff_mcp": {
      "command": "beckhoff_mcp"
    }
  }
}
```

Or with full path:
```json
{
  "mcpServers": {
    "beckhoff_mcp": {
      "command": "C:\\Python310\\Scripts\\beckhoff_mcp.exe"
    }
  }
}
```

### 3. Restart Claude Desktop

After saving the config, fully restart Claude Desktop (quit and reopen) for changes to take effect.

## Dynamic PLC Connection

Instead of editing configuration files, you can connect to remote PLCs at runtime using natural language:

### Basic Discovery (Route Pre-configured)

```
<beckhoff:mcp: ip=192.168.1.54>
```

Or simply:
```
Connect to the PLC at 192.168.1.54
```

### With Automatic Route Creation

If the ADS route doesn't exist, provide credentials:
```
Connect to PLC at 192.168.1.54 with username admin and password secret
```

The server will:
1. Send UDP discovery packet to port 48899
2. Retrieve the PLC's AMS Net ID
3. Add an ADS route (if credentials provided)
4. Connect and cache all symbols
5. Replace the current connection

## Available Tools

### beckhoff_get_device_info

Get PLC device name and version information.

**Example prompt**: "Get the Beckhoff PLC device info"

**Returns**:
```json
{
  "device_name": "PLC_Project",
  "version": {
    "version": 3,
    "revision": 1,
    "build": 4024
  }
}
```

### beckhoff_get_device_state

Get current ADS state and device state.

**Example prompt**: "What is the current state of the PLC?"

**Returns**:
```json
{
  "ads_state": {
    "code": 5,
    "name": "Run"
  },
  "device_state": 0
}
```

ADS States:
| Code | Name | Description |
|------|------|-------------|
| 0 | Invalid | Invalid state |
| 1 | Idle | Idle |
| 5 | Run | PLC is running |
| 6 | Stop | PLC is stopped |
| 15 | Config | Configuration mode |

### beckhoff_list_symbols

List available PLC symbols with optional filtering and pagination.

**Example prompts**:
- "List all PLC symbols"
- "Show symbols containing 'Motor'"
- "List symbols in the GVL namespace"

**Parameters**:
- `filter` (optional): Wildcard pattern (e.g., `*Motor*`, `GVL.*`)
- `limit` (optional): Max results (default 50)
- `offset` (optional): Pagination offset

**Returns**:
```json
{
  "symbols": [
    {"name": "MAIN.bStart", "type": "BOOL", "comment": "Start button"},
    {"name": "MAIN.nCounter", "type": "INT", "comment": "Cycle counter"}
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

### beckhoff_get_symbol_info

Get detailed information about a specific symbol.

**Example prompt**: "Get info about the symbol MAIN.bStart"

**Returns**:
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

### beckhoff_read_variable

Read a single PLC variable by symbol name.

**Example prompts**:
- "Read the value of MAIN.nCounter"
- "What is the current value of GVL.rTemperature?"

**Returns**:
```json
{
  "name": "MAIN.nCounter",
  "type": "INT",
  "value": 42
}
```

### beckhoff_read_variables

Read multiple PLC variables at once.

**Example prompt**: "Read MAIN.bStart, MAIN.bStop, and MAIN.nCounter"

**Returns**:
```json
{
  "results": [
    {"name": "MAIN.bStart", "type": "BOOL", "value": true},
    {"name": "MAIN.bStop", "type": "BOOL", "value": false},
    {"name": "MAIN.nCounter", "type": "INT", "value": 42}
  ],
  "errors": null,
  "summary": {
    "requested": 3,
    "successful": 3,
    "failed": 0
  }
}
```

### beckhoff_discover_and_connect

Discover and connect to a remote PLC by IP address using UDP discovery (port 48899).

**Example prompts**:
- `<beckhoff:mcp: ip=192.168.1.54>`
- "Connect to the PLC at 192.168.1.54"
- "Connect to PLC at 10.0.0.100 with username admin and password secret"

**Parameters**:
- `ip_address` (required): IP address of the target PLC
- `username` (optional): PLC login username for route creation
- `password` (optional): PLC login password for route creation
- `ams_port` (optional, default 851): ADS port number
- `route_name` (optional): Custom name for the ADS route

**Returns** (success):
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

**Returns** (failure):
```json
{
  "success": false,
  "error": "UDP discovery failed for 192.168.1.54: timed out",
  "hint": "Ensure the PLC is reachable and UDP port 48899 is not blocked"
}
```

### beckhoff_connection_status

Get information about the current PLC connection.

**Example prompt**: "What PLC am I connected to?"

**Returns**:
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

These tools provide read-only access to EtherCAT master and slave information via ADS port 0xFFFF (65535).

### beckhoff_get_ethercat_master_state

Get the EtherCAT master state.

**Example prompt**: "What is the EtherCAT master state?"

**Returns**:
```json
{
  "master_state": {
    "code": 8,
    "name": "Op"
  },
  "ams_net_id": "192.168.1.54.1.1",
  "ethercat_port": 65535
}
```

EtherCAT Master States:
| Code | Name | Description |
|------|------|-------------|
| 0 | Unknown | Unknown state |
| 1 | Init | Initialization |
| 2 | PreOp | Pre-Operational |
| 3 | Bootstrap | Bootstrap mode |
| 4 | SafeOp | Safe-Operational |
| 8 | Op | Operational |

### beckhoff_get_ethercat_slave_count

Get the number of EtherCAT slaves connected to the master.

**Example prompt**: "How many EtherCAT slaves are connected?"

**Returns**:
```json
{
  "slave_count": 5,
  "ams_net_id": "192.168.1.54.1.1"
}
```

### beckhoff_get_ethercat_topology

Get the EtherCAT network topology with all slave information.

**Example prompt**: "Show me the EtherCAT topology" or "List all EtherCAT slaves"

**Returns**:
```json
{
  "slave_count": 3,
  "slaves": [
    {
      "address": 1,
      "state": {"code": 8, "name": "Op"},
      "vendor_id": 2,
      "product_code": 100663346
    },
    {
      "address": 2,
      "state": {"code": 8, "name": "Op"},
      "vendor_id": 2,
      "product_code": 100728882
    },
    {
      "address": 3,
      "state": {"code": 8, "name": "Op"},
      "vendor_id": 2,
      "product_code": 100794418
    }
  ],
  "ams_net_id": "192.168.1.54.1.1"
}
```

EtherCAT Slave States:
| Code | Name | Description |
|------|------|-------------|
| 0x01 | Init | Initialization |
| 0x02 | PreOp | Pre-Operational |
| 0x03 | Bootstrap | Bootstrap mode |
| 0x04 | SafeOp | Safe-Operational |
| 0x08 | Op | Operational |
| 0x10 | Error | Error state |

### beckhoff_get_ethercat_slave_info

Get detailed information about a specific EtherCAT slave.

**Example prompt**: "Get info about EtherCAT slave 1"

**Parameters**:
- `slave_address` (required): The EtherCAT slave address (1-indexed)

**Returns**:
```json
{
  "address": 1,
  "state": {
    "code": 8,
    "name": "Op"
  },
  "vendor_id": 2,
  "product_code": 100663346,
  "crc_errors": 0,
  "ams_net_id": "192.168.1.54.1.1"
}
```

**Note**: Vendor ID 2 is Beckhoff Automation. Product codes can be looked up in the Beckhoff device catalog.

## Custom ADS Port Tools

Some EtherCAT devices (like EK1200 EtherCAT Couplers) expose their I/O data on custom ADS ports rather than the standard EtherCAT master port (0xFFFF). These tools allow you to query and read from any ADS port.

### beckhoff_query_ads_port

Query a custom ADS port to discover available symbols and device information.

**Example prompts**:
- "Query ADS port 27905"
- "What symbols are available on port 0x6D01?"
- "Check if there's an ADS server on port 27905"

**Parameters**:
- `ads_port` (required): The ADS port number (e.g., 27905 or 0x6D01)

**Returns**:
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

**How to find the ADS port**: In TwinCAT XAE, double-click on the device (e.g., under I/O > Devices > EtherCAT), go to the "ADS" tab, check "Enable ADS Server", and note the "ADS Server Port" value.

### beckhoff_read_from_port

Read a variable from a custom ADS port.

**Example prompts**:
- "Read 'Inputs.EL1008.Channel 1.Input' from port 27905"
- "Get the value of 'Outputs.EL2008.Channel 1.Output' on ADS port 27905"

**Parameters**:
- `ads_port` (required): The ADS port number (e.g., 27905)
- `symbol_name` (required): The symbol name to read

**Returns**:
```json
{
  "name": "Inputs.EL1008.Channel 1.Input",
  "type": "BOOL",
  "value": true,
  "ads_port": 27905
}
```

**When to use these tools**:
- The standard EtherCAT diagnostic tools return "Target port not found" (ADS error 6)
- You're working with EK1200 EtherCAT Couplers or similar devices
- I/O data is exposed on a custom port configured in TwinCAT XAE

## Supported Data Types

| TwinCAT Type | Description | Python Type |
|--------------|-------------|-------------|
| BOOL | Boolean | bool |
| BYTE | 8-bit unsigned | int |
| SINT | 8-bit signed | int |
| USINT | 8-bit unsigned | int |
| INT | 16-bit signed | int |
| UINT | 16-bit unsigned | int |
| DINT | 32-bit signed | int |
| UDINT | 32-bit unsigned | int |
| LINT | 64-bit signed | int |
| ULINT | 64-bit unsigned | int |
| REAL | 32-bit float | float |
| LREAL | 64-bit float | float |
| STRING | Text string | str |
| TIME, DATE, DT, TOD | Time types | int (ms) |
| WORD, DWORD, LWORD | Bit strings | int |

**Note**: Complex types (arrays, structures, function blocks) are identified but cannot be read directly. Use individual element access for structured data.

## Troubleshooting

### "Could not find module 'TcAdsDll.dll'"

**Cause**: TwinCAT ADS library not found in system PATH.

**Solution**: The MCP server automatically adds common TwinCAT paths. If you have a non-standard installation, edit `server.py`:

```python
twincat_paths = [
    r"C:\TwinCAT\Common64",
    r"C:\TwinCAT\Common32",
    r"C:\YourCustomPath",  # Add your path here
]
```

### "Failed to connect to PLC"

**Causes**:
1. TwinCAT not running
2. Wrong AMS Net ID or port
3. ADS route not configured
4. Firewall blocking ADS (port 48898)

**Solutions**:
1. Start TwinCAT System Service
2. Verify AMS Net ID in TwinCAT XAE (Target Browser)
3. Add ADS route for remote PLCs
4. Allow TCP port 48898 in firewall

### "UDP discovery failed"

**Causes**:
1. PLC not reachable on network
2. UDP port 48899 blocked by firewall
3. PLC discovery service disabled

**Solutions**:
1. Verify PLC is reachable (`ping <ip_address>`)
2. Allow UDP port 48899 in firewall (both directions)
3. Check PLC has ADS discovery enabled (default is enabled)

### "Symbol not found"

**Causes**:
1. Typo in symbol name
2. Symbol doesn't exist in running project
3. Case sensitivity issue

**Solutions**:
1. Use `beckhoff_list_symbols` to find correct name
2. Ensure PLC project is activated
3. Symbol names are case-sensitive - match exactly

### "Not connected to a PLC"

**Cause**: Server started in disconnected mode because no local PLC was available.

**Solutions**:
1. Use `beckhoff_discover_and_connect` to connect to a remote PLC
2. Start TwinCAT locally and restart the MCP server
3. Check `beckhoff_connection_status` to see current connection state

### "Target port not found" (ADS Error 6)

**Causes**:
1. EtherCAT ADS Server not enabled
2. Wrong ADS port number
3. Device uses custom ADS port instead of 0xFFFF

**Solutions**:
1. In TwinCAT XAE, double-click the device, go to "ADS" tab, enable "Enable ADS Server"
2. Note the "ADS Server Port" value (e.g., 27905)
3. Use `beckhoff_query_ads_port` with the correct port number
4. Activate the TwinCAT configuration after enabling ADS Server

### MCP Server not appearing in Claude Desktop

**Solutions**:
1. Verify config file path: `%APPDATA%\Claude\claude_desktop_config.json`
2. Check JSON syntax (no trailing commas, proper escaping)
3. Fully restart Claude Desktop (not just close window)
4. Check logs: `%APPDATA%\Claude\logs\mcp-server-beckhoff_mcp.log`

## Project Structure

```
beckhoff_mcp/
├── pyproject.toml              # Package configuration
├── README.md                   # Quick start guide
├── DOCUMENTATION.md            # This file
├── beckhoff_mcp_tasklist.md    # Original implementation plan
└── src/
    └── beckhoff_mcp/
        ├── __init__.py         # Package marker
        └── server.py           # MCP server implementation
```

## Security Considerations

- **Read-only**: The server only reads data; it cannot modify PLC variables
- **Local execution**: The MCP server runs locally on your machine
- **No network exposure**: Communication with Claude is via stdio, not network sockets
- **No authentication**: Relies on TwinCAT's ADS security model
- **Credentials in prompts**: When using `beckhoff_discover_and_connect` with credentials, be aware that username/password may be visible in conversation history

## Extending the Server

### Adding Write Capability

To add write functionality (use with caution):

```python
@mcp.tool()
def beckhoff_write_variable(ctx: Context, symbol_name: str, value: Any) -> dict[str, Any]:
    """Write a value to a PLC variable."""
    state: AppState = ctx.request_context.lifespan_context
    plc = state.plc

    symbol = state.symbols.get(symbol_name)
    if not symbol:
        return {"error": f"Symbol '{symbol_name}' not found"}

    plc_type = get_plc_type(symbol.symbol_type)
    plc.write_by_name(symbol_name, value, plc_type)

    return {"success": True, "symbol": symbol_name, "value": value}
```

### Adding Custom Tools

Follow the pattern in `server.py`:

```python
@mcp.tool(annotations=TOOL_ANNOTATIONS)
def my_custom_tool(ctx: Context, param1: str) -> dict[str, Any]:
    """Tool description shown to Claude."""
    state: AppState = ctx.request_context.lifespan_context
    plc = state.plc

    # Your implementation here
    return {"result": "..."}
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| mcp | >=1.0.0 | MCP protocol implementation |
| pyads | >=3.3.0 | Beckhoff ADS communication |
| pydantic | >=2.0.0 | Data validation |

## License

MIT License - See LICENSE file for details.

## Resources

- [MCP Documentation](https://modelcontextprotocol.io/)
- [pyads Documentation](https://pyads.readthedocs.io/)
- [Beckhoff Information System](https://infosys.beckhoff.com/)
- [TwinCAT ADS Protocol](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_ads_intro/116157835.html)
