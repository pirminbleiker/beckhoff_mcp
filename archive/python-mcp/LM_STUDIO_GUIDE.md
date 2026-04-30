# Using Beckhoff MCP with LM Studio

This guide explains how to configure LM Studio to use the Beckhoff MCP server for PLC communication.

## Prerequisites

- LM Studio installed (version with MCP support)
- Python 3.10+ installed
- Beckhoff MCP installed via pip

## Installation

### 1. Install Beckhoff MCP

```bash
# Install from source (editable mode)
pip install -e /path/to/beckhoff_mcp

# Or install from package
pip install beckhoff-mcp
```

### 2. Verify Installation

```bash
python -c "import beckhoff_mcp; print(beckhoff_mcp.__file__)"
```

This should print the path to the installed module. If you get `ModuleNotFoundError`, see [Troubleshooting](#troubleshooting).

## LM Studio Configuration

### 1. Create or Edit mcp.json

LM Studio uses an `mcp.json` file to configure MCP servers. Create this file with the following content:

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "C:\\Python310\\python.exe",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

**Important**: Replace `C:\\Python310\\python.exe` with the full path to your Python installation where beckhoff_mcp is installed.

### 2. Find Your Python Path

To find the correct Python path:

**Windows:**
```bash
where python
```

**Linux/macOS:**
```bash
which python
```

If you have multiple Python installations, verify which one has beckhoff_mcp:

```bash
# Test each Python installation
C:\Python310\python.exe -c "import beckhoff_mcp; print('OK')"
C:\Python312\python.exe -c "import beckhoff_mcp; print('OK')"
```

### 3. Load the MCP in LM Studio

1. Open LM Studio
2. Navigate to **Integrations** (puzzle piece icon)
3. Click **Install** dropdown
4. Add your MCP configuration or point to your `mcp.json` file
5. Toggle the **beckhoff** integration ON

## Configuration Examples

### Windows (Python installed globally)

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "C:\\Python310\\python.exe",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

### Windows (Python from Windows Store)

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "C:\\Users\\YourName\\AppData\\Local\\Programs\\Python\\Python311\\python.exe",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

### Linux/macOS

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "/usr/bin/python3",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

### Using Virtual Environment

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "C:\\projects\\myenv\\Scripts\\python.exe",
      "args": ["-m", "beckhoff_mcp.server"]
    }
  }
}
```

### Using UV

```json
{
  "mcpServers": {
    "beckhoff": {
      "command": "uv",
      "args": ["run", "--directory", "C:\\projects\\beckhoff_mcp\\beckhoff_mcp_v0.3.0\\beckhoff_mcp", "beckhoff-mcp"]
    }
  }
}
```

## Recommended LLM Models

MCP tool calling requires models with strong instruction-following and function-calling capabilities. Not all models perform equally well.

### Highly Recommended

| Model | Parameters | Notes |
|-------|------------|-------|
| **Qwen2.5-Instruct** | 7B, 14B, 32B | Excellent tool-calling, best overall choice |
| **Llama-3.1-Instruct** | 8B, 70B | Strong tool support, well-tested |
| **Mistral-Instruct** | 7B | Good balance of speed and capability |
| **Hermes-3** | 8B, 70B | Fine-tuned for function calling |

### Acceptable

| Model | Parameters | Notes |
|-------|------------|-------|
| **Gemma-2** | 9B, 27B | Decent tool support |
| **Phi-3** | 14B | Microsoft's capable small model |
| **DeepSeek-Coder** | 6.7B, 33B | Good for technical tasks |

### Not Recommended

| Model | Reason |
|-------|--------|
| Models < 4B parameters | Struggle with complex tool workflows |
| Base models (non-Instruct) | Not fine-tuned for instructions |
| Older model versions | Lack function-calling training |

### Model Selection Tips

1. **Start with 7B+ parameters** - Smaller models often loop or fail to process tool results
2. **Use Instruct/Chat variants** - Base models don't follow tool-calling formats
3. **Quantization matters** - Q4_K_M or higher recommended; Q2/Q3 may reduce tool accuracy
4. **Context length** - Ensure model supports enough context for your PLC's symbol list

## Troubleshooting

### "beckhoff-mcp is not recognized"

**Cause**: The command isn't in system PATH.

**Solution**: Use the full Python path instead:
```json
{
  "command": "C:\\Python310\\python.exe",
  "args": ["-m", "beckhoff_mcp.server"]
}
```

### "ModuleNotFoundError: No module named 'beckhoff_mcp'"

**Cause**: Package not installed in the Python installation LM Studio is using.

**Solutions**:

1. Verify which Python has the module:
   ```bash
   python -c "import beckhoff_mcp; print(beckhoff_mcp.__file__)"
   ```

2. Install in the correct Python:
   ```bash
   C:\Python310\python.exe -m pip install -e /path/to/beckhoff_mcp
   ```

3. Check for corrupted installations:
   ```bash
   pip show beckhoff-mcp
   ```
   Look for warnings about "invalid distribution". If found:
   ```bash
   pip uninstall beckhoff-mcp
   # Remove any ~eckhoff* folders in site-packages
   pip install -e /path/to/beckhoff_mcp
   ```

### "Connection closed" / MCP error -32000

**Cause**: The MCP server process crashed or failed to start.

**Solutions**:

1. Test the server manually:
   ```bash
   C:\Python310\python.exe -m beckhoff_mcp.server
   ```

2. Check for missing dependencies:
   ```bash
   pip install pyads mcp pydantic
   ```

3. Verify pyads can load (Windows-specific):
   ```bash
   python -c "import pyads; print(pyads.__version__)"
   ```

### Model loops on tool calls

**Cause**: The LLM model struggles with tool-calling workflows.

**Solutions**:

1. Switch to a more capable model (Qwen2.5-7B-Instruct recommended)
2. Use simpler prompts: "List available Beckhoff tools" instead of complex requests
3. Try a larger quantization (Q5 or Q6 instead of Q4)

### "Not connected to a PLC" errors

**Cause**: The MCP started in disconnected mode (no local PLC available).

**Solution**: This is normal behavior. Use the `beckhoff_discover_and_connect` tool to connect to a remote PLC:
```
Connect to the Beckhoff PLC at IP address 192.168.1.100
```

### Tools appear but don't work

**Cause**: Model generates malformed tool calls.

**Solutions**:

1. Check LM Studio logs for the exact error
2. Try a different model with better function-calling support
3. Ensure model temperature is reasonable (0.7 or lower for tool use)

## Available Tools

Once configured, these tools become available to the LLM:

| Tool | Description |
|------|-------------|
| `beckhoff_get_device_info` | Get PLC device name and version |
| `beckhoff_get_device_state` | Get current ADS state (Run, Stop, etc.) |
| `beckhoff_list_symbols` | List/search PLC symbols with pagination |
| `beckhoff_get_symbol_info` | Get detailed symbol metadata |
| `beckhoff_read_variable` | Read a single PLC variable |
| `beckhoff_read_variables` | Batch read multiple variables |
| `beckhoff_discover_and_connect` | Connect to a PLC by IP address |
| `beckhoff_connection_status` | Check current connection status |
| `beckhoff_disconnect` | Disconnect from current PLC |
| `beckhoff_get_plc_time` | Get PLC system time |
| `beckhoff_query_ads_port` | Query device info on custom ADS port |
| `beckhoff_read_from_port` | Read symbol from custom ADS port |

## Example Prompts

Once connected, try these prompts:

```
What is the current state of the PLC?
```

```
List all symbols containing "Motor"
```

```
Read the value of MAIN.bRunning
```

```
Connect to the Beckhoff PLC at 192.168.1.100
```

```
Show me the device info and current connection status
```
