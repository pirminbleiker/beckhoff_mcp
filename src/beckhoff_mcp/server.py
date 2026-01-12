"""Beckhoff PLC MCP Server - Read-only access to TwinCAT PLCs via ADS."""

import fnmatch
import os
import socket
import struct
import sys
from contextlib import asynccontextmanager
from dataclasses import dataclass
from typing import Any

# Add TwinCAT DLL directories before importing pyads
if sys.platform == "win32":
    twincat_paths = [
        r"C:\TwinCAT\Common64",
        r"C:\TwinCAT\Common32",
        r"C:\TwinCAT\AdsApi\TcAdsDll\x64",
    ]
    for path in twincat_paths:
        if os.path.isdir(path):
            os.add_dll_directory(path)

import pyads
from mcp.server.fastmcp import FastMCP, Context

# ==============================================================================
# Constants
# ==============================================================================

# Default connection settings
AMS_NET_ID = "127.0.0.1.1.1"  # Localhost AMS Net ID
AMS_PORT = pyads.PORT_TC3PLC1  # 851 - TwinCAT 3 PLC Runtime 1

# ADS state code to human-readable name mapping
ADS_STATE_NAMES = {
    0: "Invalid",
    1: "Idle",
    2: "Reset",
    3: "Init",
    4: "Start",
    5: "Run",
    6: "Stop",
    7: "SaveConfig",
    8: "LoadConfig",
    9: "PowerFailure",
    10: "PowerGood",
    11: "Error",
    12: "Shutdown",
    13: "Suspend",
    14: "Resume",
    15: "Config",
    16: "Reconfig",
    17: "Stopping",
    18: "Incompatible",
    19: "Exception",
}

# TwinCAT data type to pyads PLCTYPE mapping
TYPE_MAP = {
    "BOOL": pyads.PLCTYPE_BOOL,
    "BYTE": pyads.PLCTYPE_BYTE,
    "SINT": pyads.PLCTYPE_SINT,
    "USINT": pyads.PLCTYPE_USINT,
    "INT": pyads.PLCTYPE_INT,
    "UINT": pyads.PLCTYPE_UINT,
    "DINT": pyads.PLCTYPE_DINT,
    "UDINT": pyads.PLCTYPE_UDINT,
    "LINT": pyads.PLCTYPE_LINT,
    "ULINT": pyads.PLCTYPE_ULINT,
    "REAL": pyads.PLCTYPE_REAL,
    "LREAL": pyads.PLCTYPE_LREAL,
    "STRING": pyads.PLCTYPE_STRING,
    "TIME": pyads.PLCTYPE_UDINT,
    "DATE": pyads.PLCTYPE_UDINT,
    "DT": pyads.PLCTYPE_UDINT,
    "TOD": pyads.PLCTYPE_UDINT,
    "WORD": pyads.PLCTYPE_WORD,
    "DWORD": pyads.PLCTYPE_DWORD,
    "LWORD": pyads.PLCTYPE_ULINT,  # LWORD is 64-bit, use ULINT as equivalent
}

# Tool annotations for all read-only tools
TOOL_ANNOTATIONS = {
    "readOnlyHint": True,
    "destructiveHint": False,
    "idempotentHint": True,
    "openWorldHint": False,
}

# ==============================================================================
# EtherCAT Constants
# ==============================================================================

# EtherCAT Master ADS port
ETHERCAT_MASTER_PORT = 0xFFFF  # 65535

# EtherCAT ADS Index Groups
ETHERCAT_IDX_GRP_MASTER_STATE = 0x0003  # Master state information
ETHERCAT_IDX_GRP_SLAVE_COUNT = 0x0006  # Number of slaves
ETHERCAT_IDX_GRP_TOPOLOGY = 0x0007  # Topology information
ETHERCAT_IDX_GRP_SLAVE_STATE = 0x0009  # Slave state information
ETHERCAT_IDX_GRP_SLAVE_IDENTITY = 0x0011  # Slave identity (vendor, product)
ETHERCAT_IDX_GRP_FRAME_STATISTICS = 0x000C  # Frame statistics
ETHERCAT_IDX_GRP_CRC_ERRORS = 0x0012  # CRC error counters

# EtherCAT Master States
ETHERCAT_MASTER_STATES = {
    0: "Unknown",
    1: "Init",
    2: "PreOp",
    3: "Bootstrap",
    4: "SafeOp",
    8: "Op",
}

# EtherCAT Slave States
ETHERCAT_SLAVE_STATES = {
    0x01: "Init",
    0x02: "PreOp",
    0x03: "Bootstrap",
    0x04: "SafeOp",
    0x08: "Op",
    0x10: "Error",
}


# ==============================================================================
# Lifespan State
# ==============================================================================


@dataclass
class AppState:
    """Application state containing PLC connection and cached symbols."""

    plc: pyads.Connection | None  # None if not connected
    symbols: dict[str, Any]  # symbol_name -> symbol object
    ams_net_id: str  # Current AMS Net ID
    ams_port: int  # Current AMS port
    ip_address: str | None = None  # Remote IP (None for local connection)
    connected: bool = False  # Whether we have an active connection


# ==============================================================================
# Helper Functions
# ==============================================================================


def get_plc_type(type_name: str) -> Any:
    """Map TwinCAT type string to pyads PLCTYPE constant.

    Args:
        type_name: The TwinCAT data type name (e.g., "BOOL", "INT", "REAL")

    Returns:
        The corresponding pyads PLCTYPE constant.

    Raises:
        ValueError: If the type is not supported.
    """
    # Normalize type name (uppercase, strip array notation)
    normalized = type_name.upper().split("[")[0].strip()

    # Handle STRING with length specifier (e.g., "STRING(80)")
    if normalized.startswith("STRING"):
        return pyads.PLCTYPE_STRING

    if normalized in TYPE_MAP:
        return TYPE_MAP[normalized]

    raise ValueError(f"Unsupported PLC type: {type_name}")


def get_state_name(state_code: int) -> str:
    """Map ADS state code to human-readable name.

    Args:
        state_code: The ADS state code.

    Returns:
        Human-readable state name.
    """
    return ADS_STATE_NAMES.get(state_code, f"Unknown({state_code})")


def get_local_ams_net_id() -> str:
    """Generate a local AMS Net ID based on the machine's IP address.

    Convention: {ip_address}.1.1
    Example: 192.168.1.100.1.1

    Returns:
        A valid AMS Net ID string.
    """
    try:
        # Get local IP by connecting to an external address (doesn't actually send data)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        local_ip = s.getsockname()[0]
        s.close()
        return f"{local_ip}.1.1"
    except Exception:
        return "127.0.0.1.1.1"  # Fallback to localhost


def refresh_symbols(plc: pyads.Connection) -> dict[str, Any]:
    """Fetch and return all symbols from a PLC connection.

    Args:
        plc: An open pyads.Connection

    Returns:
        Dictionary mapping symbol names to symbol objects

    Raises:
        pyads.ADSError: If symbol retrieval fails
    """
    all_symbols = plc.get_all_symbols()
    return {sym.name: sym for sym in all_symbols}


def format_symbol(symbol: Any) -> dict[str, Any]:
    """Format a pyads symbol object for output.

    Args:
        symbol: A pyads Symbol object.

    Returns:
        Dictionary with symbol information.
    """
    return {
        "name": symbol.name,
        "type": symbol.symbol_type,
        "comment": symbol.comment or "",
    }


def format_symbol_detailed(symbol: Any) -> dict[str, Any]:
    """Format a pyads symbol object with detailed information.

    Args:
        symbol: A pyads Symbol object.

    Returns:
        Dictionary with detailed symbol information.
    """
    return {
        "name": symbol.name,
        "type": symbol.symbol_type,
        "index_group": symbol.index_group,
        "index_offset": symbol.index_offset,
        "size": symbol.size,
        "comment": symbol.comment or "",
    }


# ==============================================================================
# Lifespan Context Manager
# ==============================================================================


@asynccontextmanager
async def lifespan(server: FastMCP):
    """Manage PLC connection lifecycle.

    Attempts to connect to the local PLC on startup, but starts in disconnected
    mode if the local PLC is unavailable. Use beckhoff_discover_and_connect to
    connect to a remote PLC.
    """
    plc = None
    symbols = {}
    connected = False

    # Try to connect to local PLC, but don't fail if unavailable
    try:
        plc = pyads.Connection(AMS_NET_ID, AMS_PORT)
        plc.open()
        symbols = refresh_symbols(plc)
        connected = True
    except pyads.ADSError:
        # Local PLC not available - start in disconnected mode
        # User can connect later with beckhoff_discover_and_connect
        if plc is not None and plc.is_open:
            plc.close()
        plc = None

    state = AppState(
        plc=plc,
        symbols=symbols,
        ams_net_id=AMS_NET_ID,
        ams_port=AMS_PORT,
        ip_address=None,
        connected=connected,
    )

    try:
        yield state
    finally:
        if state.plc is not None and state.plc.is_open:
            state.plc.close()


# ==============================================================================
# MCP Server Setup
# ==============================================================================

mcp = FastMCP("beckhoff_mcp", lifespan=lifespan)


# ==============================================================================
# Tools
# ==============================================================================


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_device_info(ctx: Context) -> dict[str, Any]:
    """Get PLC device name and version information.

    Returns the device name and version (version, revision, build) of the connected PLC.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected or state.plc is None:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    try:
        name, version = state.plc.read_device_info()
        return {
            "device_name": name,
            "version": {
                "version": version.version,
                "revision": version.revision,
                "build": version.build,
            },
        }
    except pyads.ADSError as e:
        return {"error": f"Failed to read device info: {e}"}


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_device_state(ctx: Context) -> dict[str, Any]:
    """Get current ADS state and device state.

    Returns the ADS state (Run, Stop, Config, etc.) and device state
    with both numeric codes and human-readable names.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected or state.plc is None:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    try:
        ads_state, device_state = state.plc.read_state()
        return {
            "ads_state": {
                "code": ads_state,
                "name": get_state_name(ads_state),
            },
            "device_state": device_state,
        }
    except pyads.ADSError as e:
        return {"error": f"Failed to read device state: {e}"}


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_list_symbols(
    ctx: Context,
    filter: str | None = None,
    limit: int = 50,
    offset: int = 0,
) -> dict[str, Any]:
    """List available PLC symbols with types.

    Args:
        filter: Optional filter pattern (supports wildcards like "GVL_*" or "*Motor*").
        limit: Maximum number of results to return (default 50).
        offset: Pagination offset for retrieving additional results.

    Returns a paginated list of symbols with name, type, and comment.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    symbols = state.symbols

    # Get all symbol names
    all_names = list(symbols.keys())

    # Apply filter if provided
    if filter:
        all_names = [
            name for name in all_names if fnmatch.fnmatch(name.lower(), filter.lower())
        ]

    # Sort for consistent results
    all_names.sort()

    # Calculate pagination
    total = len(all_names)
    paginated_names = all_names[offset : offset + limit]
    has_more = offset + limit < total

    # Format symbols
    formatted_symbols = [format_symbol(symbols[name]) for name in paginated_names]

    return {
        "symbols": formatted_symbols,
        "pagination": {
            "total": total,
            "offset": offset,
            "limit": limit,
            "returned": len(formatted_symbols),
            "has_more": has_more,
            "next_offset": offset + limit if has_more else None,
        },
    }


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_symbol_info(ctx: Context, symbol_name: str) -> dict[str, Any]:
    """Get detailed information about a specific symbol.

    Args:
        symbol_name: The full name of the symbol (e.g., "MAIN.bStart" or "GVL.nCounter").

    Returns detailed symbol info including index_group, index_offset, data_type, and size.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected or state.plc is None:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    symbols = state.symbols

    # Try cache first
    if symbol_name in symbols:
        return format_symbol_detailed(symbols[symbol_name])

    # Try fetching from PLC (in case cache is stale)
    try:
        symbol = state.plc.get_symbol(symbol_name)
        return format_symbol_detailed(symbol)
    except pyads.ADSError as e:
        return {"error": f"Symbol '{symbol_name}' not found: {e}"}


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_read_variable(ctx: Context, symbol_name: str) -> dict[str, Any]:
    """Read a single PLC variable by symbol name.

    Args:
        symbol_name: The full name of the variable (e.g., "MAIN.bStart" or "GVL.nCounter").

    Returns the variable name, current value, and data type.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected or state.plc is None:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    symbols = state.symbols

    # Get symbol info to determine type
    symbol = symbols.get(symbol_name)
    if not symbol:
        try:
            symbol = state.plc.get_symbol(symbol_name)
        except pyads.ADSError as e:
            return {"error": f"Symbol '{symbol_name}' not found: {e}"}

    # Get the PLC type
    try:
        plc_type = get_plc_type(symbol.symbol_type)
    except ValueError:
        # For complex types (arrays, structs), try reading as raw or return info
        return {
            "name": symbol_name,
            "type": symbol.symbol_type,
            "value": None,
            "error": f"Complex type '{symbol.symbol_type}' - use raw read or inspect structure",
        }

    # Read the value
    try:
        value = state.plc.read_by_name(symbol_name, plc_type)
        return {
            "name": symbol_name,
            "type": symbol.symbol_type,
            "value": value,
        }
    except pyads.ADSError as e:
        return {"error": f"Failed to read '{symbol_name}': {e}"}


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_read_variables(ctx: Context, symbol_names: list[str]) -> dict[str, Any]:
    """Read multiple PLC variables at once.

    Args:
        symbol_names: List of variable names to read (e.g., ["MAIN.bStart", "GVL.nCounter"]).

    Returns a list of results for each variable with name, value, and type.
    Individual read errors are reported per-variable without failing the entire request.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected or state.plc is None:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    symbols = state.symbols
    results = []
    errors = []

    for symbol_name in symbol_names:
        # Get symbol info
        symbol = symbols.get(symbol_name)
        if not symbol:
            try:
                symbol = state.plc.get_symbol(symbol_name)
            except pyads.ADSError:
                errors.append({"name": symbol_name, "error": "Symbol not found"})
                continue

        # Get PLC type
        try:
            plc_type = get_plc_type(symbol.symbol_type)
        except ValueError:
            errors.append(
                {
                    "name": symbol_name,
                    "type": symbol.symbol_type,
                    "error": f"Unsupported complex type",
                }
            )
            continue

        # Read value
        try:
            value = state.plc.read_by_name(symbol_name, plc_type)
            results.append(
                {
                    "name": symbol_name,
                    "type": symbol.symbol_type,
                    "value": value,
                }
            )
        except pyads.ADSError as e:
            errors.append({"name": symbol_name, "error": str(e)})

    return {
        "results": results,
        "errors": errors if errors else None,
        "summary": {
            "requested": len(symbol_names),
            "successful": len(results),
            "failed": len(errors),
        },
    }


# Tool annotations for connection management tools (not read-only)
CONNECTION_TOOL_ANNOTATIONS = {
    "readOnlyHint": False,
    "destructiveHint": False,
    "idempotentHint": True,
    "openWorldHint": True,
}


@mcp.tool(annotations=CONNECTION_TOOL_ANNOTATIONS)
def beckhoff_discover_and_connect(
    ctx: Context,
    ip_address: str,
    username: str | None = None,
    password: str | None = None,
    ams_port: int = 851,
    route_name: str | None = None,
) -> dict[str, Any]:
    """Discover and connect to a remote Beckhoff PLC by IP address.

    Uses UDP discovery (port 48899) to find the PLC's AMS Net ID and optionally
    adds an ADS route. Replaces the current PLC connection with the new one
    and re-caches all symbols.

    Args:
        ip_address: IP address of the target PLC (e.g., "192.168.1.54")
        username: PLC login username for route creation (optional)
        password: PLC login password for route creation (optional)
        ams_port: ADS port number (default 851 for PLC Runtime 1)
        route_name: Name for the route on the PLC side (defaults to hostname)

    Returns:
        Connection information including discovered AMS Net ID, device info,
        and symbol count.
    """
    state: AppState = ctx.request_context.lifespan_context
    old_plc = state.plc

    # Step 1: Discover AMS Net ID via UDP
    try:
        # Use pyads_ex submodule which has the discovery function
        from pyads.pyads_ex import adsGetNetIdForPLC
        discovered_ams_net_id = adsGetNetIdForPLC(ip_address)
    except ImportError:
        return {
            "success": False,
            "error": "UDP discovery not available in this pyads version",
            "hint": "Update pyads to 3.3.0+ or provide AMS Net ID directly",
        }
    except Exception as e:
        return {
            "success": False,
            "error": f"UDP discovery failed for {ip_address}: {e}",
            "hint": "Ensure the PLC is reachable and UDP port 48899 is not blocked",
        }

    # Step 2: Add route to PLC if credentials provided (Linux only)
    route_added = False
    route_error = None
    if username is not None and password is not None:
        if sys.platform != "win32":
            # Route creation only works on Linux - Windows uses TwinCAT router
            try:
                local_ams_net_id = get_local_ams_net_id()
                hostname = socket.gethostname()

                pyads.open_port()
                pyads.set_local_address(local_ams_net_id)

                route_added = pyads.add_route_to_plc(
                    sending_net_id=local_ams_net_id,
                    adding_host_name=hostname,
                    ip_address=ip_address,
                    username=username,
                    password=password,
                    route_name=route_name or hostname,
                    added_net_id=None,
                )

                pyads.close_port()
            except Exception as e:
                try:
                    pyads.close_port()
                except Exception:
                    pass
                route_error = str(e)
        else:
            # On Windows, route must be added via TwinCAT router
            route_error = "Route creation not supported on Windows. Add route manually via TwinCAT."
    elif username is not None or password is not None:
        route_error = "Both username and password must be provided for route creation"

    # Step 3: Create new connection
    new_plc = None
    try:
        new_plc = pyads.Connection(discovered_ams_net_id, ams_port, ip_address)
        new_plc.open()
    except pyads.ADSError as e:
        if new_plc is not None:
            try:
                new_plc.close()
            except Exception:
                pass
        return {
            "success": False,
            "error": f"Failed to connect to PLC at {discovered_ams_net_id}:{ams_port}: {e}",
            "discovered_ams_net_id": discovered_ams_net_id,
            "route_added": route_added,
            "hint": "ADS route may not be configured. Provide username/password to add route.",
        }

    # Step 4: Get device info from new connection
    device_info = None
    try:
        device_name, version = new_plc.read_device_info()
        device_info = {
            "device_name": device_name,
            "version": {
                "version": version.version,
                "revision": version.revision,
                "build": version.build,
            },
        }
    except pyads.ADSError:
        pass  # Device info is optional

    # Step 5: Cache symbols from new PLC
    try:
        new_symbols = refresh_symbols(new_plc)
    except pyads.ADSError as e:
        new_plc.close()
        return {
            "success": False,
            "error": f"Failed to retrieve symbols from PLC: {e}",
            "discovered_ams_net_id": discovered_ams_net_id,
            "device_info": device_info,
        }

    # Step 6: Close old connection and update state
    if old_plc is not None and old_plc.is_open:
        old_plc.close()

    # Update the state in-place
    state.plc = new_plc
    state.symbols = new_symbols
    state.ams_net_id = discovered_ams_net_id
    state.ams_port = ams_port
    state.ip_address = ip_address
    state.connected = True

    result = {
        "success": True,
        "ip_address": ip_address,
        "discovered_ams_net_id": discovered_ams_net_id,
        "ams_port": ams_port,
        "route_added": route_added,
        "device_info": device_info,
        "symbols_cached": len(new_symbols),
    }
    if route_error:
        result["route_note"] = route_error
    return result


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_connection_status(ctx: Context) -> dict[str, Any]:
    """Get information about the current PLC connection.

    Returns connection details including AMS Net ID, port, IP address (if remote),
    and whether the connection is active.
    """
    state: AppState = ctx.request_context.lifespan_context

    is_connected = state.connected and state.plc is not None and state.plc.is_open

    return {
        "ams_net_id": state.ams_net_id,
        "ams_port": state.ams_port,
        "ip_address": state.ip_address,
        "is_remote": state.ip_address is not None,
        "is_connected": is_connected,
        "symbols_cached": len(state.symbols),
    }


# ==============================================================================
# EtherCAT Diagnostic Tools
# ==============================================================================


def get_ethercat_master_state_name(state_code: int) -> str:
    """Map EtherCAT master state code to human-readable name."""
    return ETHERCAT_MASTER_STATES.get(state_code, f"Unknown({state_code})")


def get_ethercat_slave_state_name(state_code: int) -> str:
    """Map EtherCAT slave state code to human-readable name."""
    return ETHERCAT_SLAVE_STATES.get(state_code & 0x0F, f"Unknown({state_code})")


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_ethercat_master_state(ctx: Context) -> dict[str, Any]:
    """Get the EtherCAT master state and basic information.

    Queries the EtherCAT master via ADS port 0xFFFF (65535) to retrieve
    the current master state (Init, PreOp, SafeOp, Op).

    Returns:
        Master state with numeric code and human-readable name.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    # Create connection to EtherCAT master (port 0xFFFF)
    try:
        ec_plc = pyads.Connection(
            state.ams_net_id,
            ETHERCAT_MASTER_PORT,
            state.ip_address,
        )
        ec_plc.open()
    except pyads.ADSError as e:
        return {
            "error": f"Failed to connect to EtherCAT master: {e}",
            "hint": "Ensure EtherCAT master is configured and running",
        }

    try:
        # Read master state: Index Group 0x0003, Index Offset 0x0000
        # Returns a WORD (2 bytes) with the state
        raw_data = ec_plc.read(
            ETHERCAT_IDX_GRP_MASTER_STATE,
            0x0000,
            pyads.PLCTYPE_UINT,
        )
        master_state = raw_data

        return {
            "master_state": {
                "code": master_state,
                "name": get_ethercat_master_state_name(master_state),
            },
            "ams_net_id": state.ams_net_id,
            "ethercat_port": ETHERCAT_MASTER_PORT,
        }
    except pyads.ADSError as e:
        error_code = getattr(e, "err_code", None)
        if error_code == 6:  # Target port not found
            return {
                "error": f"EtherCAT master port (0xFFFF) not accessible: {e}",
                "hint": "The ADS Server for EtherCAT may not be enabled. In TwinCAT XAE: "
                "double-click 'Image' under EtherCAT Master, go to 'ADS' tab, "
                "enable 'Enable ADS Server' and 'Create symbols', then activate configuration.",
                "possible_causes": [
                    "No EtherCAT master configured on this PLC",
                    "ADS Server not enabled for EtherCAT device",
                    "Remote ADS access to EtherCAT may be restricted",
                ],
            }
        return {
            "error": f"Failed to read EtherCAT master state: {e}",
            "hint": "The PLC may not have EtherCAT configured",
        }
    finally:
        if ec_plc.is_open:
            ec_plc.close()


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_ethercat_slave_count(ctx: Context) -> dict[str, Any]:
    """Get the number of EtherCAT slaves connected to the master.

    Queries the EtherCAT master to retrieve the total count of configured
    and connected slaves.

    Returns:
        Number of configured EtherCAT slaves.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    try:
        ec_plc = pyads.Connection(
            state.ams_net_id,
            ETHERCAT_MASTER_PORT,
            state.ip_address,
        )
        ec_plc.open()
    except pyads.ADSError as e:
        return {
            "error": f"Failed to connect to EtherCAT master: {e}",
        }

    try:
        # Read slave count: Index Group 0x0006, Index Offset 0x0000
        # Returns a DWORD (4 bytes) with the count
        slave_count = ec_plc.read(
            ETHERCAT_IDX_GRP_SLAVE_COUNT,
            0x0000,
            pyads.PLCTYPE_UDINT,
        )

        return {
            "slave_count": slave_count,
            "ams_net_id": state.ams_net_id,
        }
    except pyads.ADSError as e:
        error_code = getattr(e, "err_code", None)
        if error_code == 6:  # Target port not found
            return {
                "error": f"EtherCAT master port (0xFFFF) not accessible: {e}",
                "hint": "The ADS Server for EtherCAT may not be enabled. In TwinCAT XAE: "
                "double-click 'Image' under EtherCAT Master, go to 'ADS' tab, "
                "enable 'Enable ADS Server' and 'Create symbols', then activate configuration.",
                "possible_causes": [
                    "No EtherCAT master configured on this PLC",
                    "ADS Server not enabled for EtherCAT device",
                    "Remote ADS access to EtherCAT may be restricted",
                ],
            }
        return {
            "error": f"Failed to read slave count: {e}",
            "hint": "The PLC may not have EtherCAT configured",
        }
    finally:
        if ec_plc.is_open:
            ec_plc.close()


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_ethercat_topology(ctx: Context) -> dict[str, Any]:
    """Get the EtherCAT network topology and slave list.

    Queries each configured slave to build a topology view showing
    slave addresses, states, and basic identification.

    Returns:
        List of slaves with address, state, and identification info.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    try:
        ec_plc = pyads.Connection(
            state.ams_net_id,
            ETHERCAT_MASTER_PORT,
            state.ip_address,
        )
        ec_plc.open()
    except pyads.ADSError as e:
        return {
            "error": f"Failed to connect to EtherCAT master: {e}",
        }

    slaves = []
    try:
        # First get slave count
        slave_count = ec_plc.read(
            ETHERCAT_IDX_GRP_SLAVE_COUNT,
            0x0000,
            pyads.PLCTYPE_UDINT,
        )

        # Query each slave (1-indexed)
        for slave_addr in range(1, slave_count + 1):
            slave_info = {"address": slave_addr}

            # Read slave state: Index Group 0x0009, Index Offset = slave address
            try:
                slave_state = ec_plc.read(
                    ETHERCAT_IDX_GRP_SLAVE_STATE,
                    slave_addr,
                    pyads.PLCTYPE_UINT,
                )
                slave_info["state"] = {
                    "code": slave_state,
                    "name": get_ethercat_slave_state_name(slave_state),
                }
            except pyads.ADSError:
                slave_info["state"] = {"error": "Could not read state"}

            # Read slave identity: Index Group 0x0011, Index Offset = slave address
            # This typically returns vendor ID and product code
            try:
                # Try reading 8 bytes: vendor ID (4) + product code (4)
                raw_identity = ec_plc.read(
                    ETHERCAT_IDX_GRP_SLAVE_IDENTITY,
                    slave_addr,
                    pyads.PLCTYPE_UDINT * 2,
                )
                if isinstance(raw_identity, (list, tuple)) and len(raw_identity) >= 2:
                    slave_info["vendor_id"] = raw_identity[0]
                    slave_info["product_code"] = raw_identity[1]
                else:
                    slave_info["identity_raw"] = raw_identity
            except pyads.ADSError:
                # Identity read may not be supported on all systems
                pass

            slaves.append(slave_info)

        return {
            "slave_count": slave_count,
            "slaves": slaves,
            "ams_net_id": state.ams_net_id,
        }
    except pyads.ADSError as e:
        error_code = getattr(e, "err_code", None)
        if error_code == 6:  # Target port not found
            return {
                "error": f"EtherCAT master port (0xFFFF) not accessible: {e}",
                "hint": "The ADS Server for EtherCAT may not be enabled. In TwinCAT XAE: "
                "double-click 'Image' under EtherCAT Master, go to 'ADS' tab, "
                "enable 'Enable ADS Server' and 'Create symbols', then activate configuration.",
                "possible_causes": [
                    "No EtherCAT master configured on this PLC",
                    "ADS Server not enabled for EtherCAT device",
                    "Remote ADS access to EtherCAT may be restricted",
                ],
            }
        return {
            "error": f"Failed to read topology: {e}",
            "slaves_found": slaves,
        }
    finally:
        if ec_plc.is_open:
            ec_plc.close()


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_get_ethercat_slave_info(
    ctx: Context,
    slave_address: int,
) -> dict[str, Any]:
    """Get detailed information about a specific EtherCAT slave.

    Args:
        slave_address: The EtherCAT slave address (1-indexed).

    Returns:
        Detailed slave information including state, identity, and diagnostics.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    if slave_address < 1:
        return {"error": "Slave address must be >= 1 (1-indexed)"}

    try:
        ec_plc = pyads.Connection(
            state.ams_net_id,
            ETHERCAT_MASTER_PORT,
            state.ip_address,
        )
        ec_plc.open()
    except pyads.ADSError as e:
        return {
            "error": f"Failed to connect to EtherCAT master: {e}",
        }

    result = {"address": slave_address}
    try:
        # Read slave state
        try:
            slave_state = ec_plc.read(
                ETHERCAT_IDX_GRP_SLAVE_STATE,
                slave_address,
                pyads.PLCTYPE_UINT,
            )
            result["state"] = {
                "code": slave_state,
                "name": get_ethercat_slave_state_name(slave_state),
            }
        except pyads.ADSError as e:
            result["state_error"] = str(e)

        # Read slave identity
        try:
            raw_identity = ec_plc.read(
                ETHERCAT_IDX_GRP_SLAVE_IDENTITY,
                slave_address,
                pyads.PLCTYPE_UDINT * 2,
            )
            if isinstance(raw_identity, (list, tuple)) and len(raw_identity) >= 2:
                result["vendor_id"] = raw_identity[0]
                result["product_code"] = raw_identity[1]
        except pyads.ADSError:
            pass

        # Try to read CRC error counters for this slave
        try:
            crc_errors = ec_plc.read(
                ETHERCAT_IDX_GRP_CRC_ERRORS,
                slave_address,
                pyads.PLCTYPE_UDINT,
            )
            result["crc_errors"] = crc_errors
        except pyads.ADSError:
            # CRC read may not be supported
            pass

        result["ams_net_id"] = state.ams_net_id
        return result

    except pyads.ADSError as e:
        error_code = getattr(e, "err_code", None)
        if error_code == 6:  # Target port not found
            return {
                "error": f"EtherCAT master port (0xFFFF) not accessible: {e}",
                "hint": "The ADS Server for EtherCAT may not be enabled. In TwinCAT XAE: "
                "double-click 'Image' under EtherCAT Master, go to 'ADS' tab, "
                "enable 'Enable ADS Server' and 'Create symbols', then activate configuration.",
                "possible_causes": [
                    "No EtherCAT master configured on this PLC",
                    "ADS Server not enabled for EtherCAT device",
                    "Remote ADS access to EtherCAT may be restricted",
                ],
            }
        result["error"] = f"Failed to read slave info: {e}"
        return result
    finally:
        if ec_plc.is_open:
            ec_plc.close()


# ==============================================================================
# Custom ADS Port Tools
# ==============================================================================


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_query_ads_port(
    ctx: Context,
    ads_port: int,
) -> dict[str, Any]:
    """Query a custom ADS port on the connected PLC.

    Use this tool when the standard EtherCAT master port (0xFFFF) does not work.
    Some devices like EK1200 EtherCAT Couplers expose their I/O data on custom
    ADS ports (e.g., 27905 / 0x6D01) configured in TwinCAT XAE under the device's
    ADS tab.

    Args:
        ads_port: The ADS port number to query (e.g., 27905 or 0x6D01).

    Returns:
        Device info, state, and available symbols on the specified port.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    result = {
        "ads_port": ads_port,
        "ads_port_hex": f"0x{ads_port:04X}",
        "ams_net_id": state.ams_net_id,
        "ip_address": state.ip_address,
    }

    # Create connection to the custom port
    try:
        custom_plc = pyads.Connection(
            state.ams_net_id,
            ads_port,
            state.ip_address,
        )
        custom_plc.open()
    except pyads.ADSError as e:
        return {
            **result,
            "success": False,
            "error": f"Failed to connect to port {ads_port}: {e}",
            "hint": "Verify the port number is correct. Check TwinCAT XAE under "
            "the device's ADS tab to find the configured ADS Server port.",
        }

    try:
        # Try to read device info
        try:
            name, version = custom_plc.read_device_info()
            result["device_info"] = {
                "device_name": name,
                "version": {
                    "version": version.version,
                    "revision": version.revision,
                    "build": version.build,
                },
            }
        except pyads.ADSError:
            result["device_info"] = None

        # Try to read state
        try:
            ads_state, device_state = custom_plc.read_state()
            result["state"] = {
                "ads_state": {
                    "code": ads_state,
                    "name": get_state_name(ads_state),
                },
                "device_state": device_state,
            }
        except pyads.ADSError:
            result["state"] = None

        # Try to get symbols
        try:
            symbols = custom_plc.get_all_symbols()
            result["symbol_count"] = len(symbols)
            # Include first 20 symbols as preview
            result["symbols_preview"] = [
                {"name": sym.name, "type": sym.symbol_type}
                for sym in list(symbols)[:20]
            ]
            if len(symbols) > 20:
                result["symbols_note"] = f"Showing first 20 of {len(symbols)} symbols"
        except pyads.ADSError:
            result["symbol_count"] = 0
            result["symbols_preview"] = []

        result["success"] = True
        return result

    except pyads.ADSError as e:
        error_code = getattr(e, "err_code", None)
        if error_code == 6:  # Target port not found
            return {
                **result,
                "success": False,
                "error": f"Port {ads_port} not found: {e}",
                "hint": "This port may not exist or the ADS Server is not enabled.",
            }
        return {
            **result,
            "success": False,
            "error": f"Failed to query port {ads_port}: {e}",
        }
    finally:
        if custom_plc.is_open:
            custom_plc.close()


@mcp.tool(annotations=TOOL_ANNOTATIONS)
def beckhoff_read_from_port(
    ctx: Context,
    ads_port: int,
    symbol_name: str,
) -> dict[str, Any]:
    """Read a variable from a custom ADS port.

    Use this to read I/O data from devices that expose symbols on custom ADS
    ports rather than the standard PLC port (851).

    Args:
        ads_port: The ADS port number (e.g., 27905).
        symbol_name: The symbol name to read (e.g., "Inputs.EL1008.Channel 1.Input").

    Returns:
        The variable name, type, and value.
    """
    state: AppState = ctx.request_context.lifespan_context

    if not state.connected:
        return {
            "error": "Not connected to a PLC",
            "hint": "Use beckhoff_discover_and_connect to connect to a PLC first",
        }

    # Create connection to the custom port
    try:
        custom_plc = pyads.Connection(
            state.ams_net_id,
            ads_port,
            state.ip_address,
        )
        custom_plc.open()
    except pyads.ADSError as e:
        return {
            "error": f"Failed to connect to port {ads_port}: {e}",
            "ads_port": ads_port,
        }

    try:
        # Get symbol info
        try:
            symbol = custom_plc.get_symbol(symbol_name)
        except pyads.ADSError as e:
            return {
                "error": f"Symbol '{symbol_name}' not found on port {ads_port}: {e}",
                "ads_port": ads_port,
            }

        # Get PLC type
        try:
            plc_type = get_plc_type(symbol.symbol_type)
        except ValueError:
            return {
                "name": symbol_name,
                "type": symbol.symbol_type,
                "value": None,
                "ads_port": ads_port,
                "error": f"Complex type '{symbol.symbol_type}' - direct read not supported",
            }

        # Read the value
        try:
            value = custom_plc.read_by_name(symbol_name, plc_type)
            return {
                "name": symbol_name,
                "type": symbol.symbol_type,
                "value": value,
                "ads_port": ads_port,
            }
        except pyads.ADSError as e:
            return {
                "error": f"Failed to read '{symbol_name}': {e}",
                "ads_port": ads_port,
            }

    finally:
        if custom_plc.is_open:
            custom_plc.close()


# ==============================================================================
# Main Entry Point
# ==============================================================================


def main():
    """Run the MCP server."""
    mcp.run()


if __name__ == "__main__":
    main()
