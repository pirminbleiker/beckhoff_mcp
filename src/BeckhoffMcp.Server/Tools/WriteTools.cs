using System.ComponentModel;
using System.Globalization;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class WriteTools
{
    private static readonly SymbolLoaderSettings FlatSettings = new(
        SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred);

    private readonly AdsConnectionManager _ads;
    public WriteTools(AdsConnectionManager ads) => _ads = ads;

    private static ISymbol? FindSymbol(ISymbolCollection<ISymbol> symbols, string name)
    {
        foreach (var s in symbols)
            if (s.InstancePath.Equals(name, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    [McpServerTool(Name = "beckhoff_write_variable"),
     Description("Write a value to a single PLC variable. The string value is auto-converted to the symbol's PLC type " +
                 "(BOOL: 'true'/'false'; integer types: decimal; REAL/LREAL: dot decimals; STRING/WSTRING: as-is; " +
                 "ENUM: name or numeric value).")]
    public async Task<object> WriteVariable(
        [Description("Full symbol path, e.g. 'MAIN.bStart' or 'GVL.nCounter'")] string symbol_name,
        [Description("Value as string — auto-converted to the variable's PLC type.")] string value,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
            var symbol = FindSymbol(loader.Symbols, symbol_name);
            if (symbol is null)
                return new { name = symbol_name, ok = false, error = "not_found" };

            var typed = ValueParser.Parse(value, symbol);
            if (symbol is not IValueSymbol valueSymbol)
                return new { name = symbol_name, ok = false, error = "not_writable" };

            valueSymbol.WriteValue(typed);
            return new { name = symbol.InstancePath, type = symbol.TypeName, written = value, ok = true };
        }
        catch (Exception ex)
        {
            return new { name = symbol_name, ok = false, error = ex.Message };
        }
    }

    public sealed record WriteRequest(string Path, string Value);

    [McpServerTool(Name = "beckhoff_write_variables"),
     Description("Write multiple PLC variables in a single batch. Returns per-item success/failure.")]
    public async Task<object> WriteVariables(
        [Description("Array of {path, value} pairs. Each value is auto-converted to its symbol's PLC type.")] WriteRequest[] requests,
        CancellationToken ct = default)
    {
        if (requests is null || requests.Length == 0)
            return new { ok = false, error = "no requests" };

        var conn = _ads.EnsureConnected();
        var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
        var results = new List<object>();
        var successCount = 0;

        foreach (var req in requests)
        {
            try
            {
                var symbol = FindSymbol(loader.Symbols, req.Path);
                if (symbol is null)
                {
                    results.Add(new { path = req.Path, ok = false, error = "not_found" });
                    continue;
                }
                if (symbol is not IValueSymbol valueSymbol)
                {
                    results.Add(new { path = req.Path, ok = false, error = "not_writable" });
                    continue;
                }
                var typed = ValueParser.Parse(req.Value, symbol);
                valueSymbol.WriteValue(typed);
                results.Add(new { path = symbol.InstancePath, ok = true });
                successCount++;
            }
            catch (Exception ex)
            {
                results.Add(new { path = req.Path, ok = false, error = ex.Message });
            }
        }
        return new
        {
            success_count = successCount,
            failure_count = requests.Length - successCount,
            results,
        };
    }

    [McpServerTool(Name = "beckhoff_write_control"),
     Description("Change PLC ADS state at runtime: 'run' / 'stop' / 'config' / 'reset'. " +
                 "Uses the System Service (port 10000) of the active target. Use with caution — affects PLC operation.")]
    public async Task<object> WriteControl(
        [Description("Target state name: 'run', 'stop', 'config', or 'reset'.")] string target_state,
        CancellationToken ct = default)
    {
        AdsState ads;
        switch (target_state.Trim().ToLowerInvariant())
        {
            case "run":    ads = AdsState.Run; break;
            case "stop":   ads = AdsState.Stop; break;
            case "config": ads = AdsState.Config; break;
            case "reset":  ads = AdsState.Reset; break;
            default:
                return new { ok = false, error = $"unknown state '{target_state}' (expected: run/stop/config/reset)" };
        }
        try
        {
            var sys = _ads.EnsureConnectedToPort(10000);
            var current = await sys.ReadStateAsync(ct);
            ushort deviceState = current.Succeeded ? (ushort)current.State.DeviceState : (ushort)0;

            var result = await sys.WriteControlAsync(ads, deviceState, ct);
            if (result.ErrorCode != AdsErrorCode.NoError)
                return new { ok = false, requested_state = ads.ToString(), error = result.ErrorCode.ToString() };

            return new
            {
                ok = true,
                requested_state = ads.ToString(),
                message = $"WriteControl({ads}) sent via SystemService:10000",
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, requested_state = ads.ToString(), error = ex.Message };
        }
    }
}

/// <summary>Converts a user-supplied string value to the typed value expected by an ADS symbol.</summary>
internal static class ValueParser
{
    public static object Parse(string value, ISymbol symbol)
    {
        var typeName = symbol.DataType?.Name?.ToUpperInvariant() ?? "";
        var category = symbol.DataType?.Category ?? DataTypeCategory.Primitive;

        if (category == DataTypeCategory.Enum && symbol.DataType is IEnumType enumType)
        {
            foreach (var field in enumType.EnumValues)
                if (string.Equals(field.Name, value, StringComparison.OrdinalIgnoreCase))
                    return field.Value;
            // fall through to numeric parse
        }

        return typeName switch
        {
            "BOOL" => bool.Parse(value),
            "BYTE" or "USINT" => byte.Parse(value, CultureInfo.InvariantCulture),
            "SBYTE" or "SINT" => sbyte.Parse(value, CultureInfo.InvariantCulture),
            "INT" or "INT16" => short.Parse(value, CultureInfo.InvariantCulture),
            "UINT" or "UINT16" or "WORD" => ushort.Parse(value, CultureInfo.InvariantCulture),
            "DINT" or "INT32" => int.Parse(value, CultureInfo.InvariantCulture),
            "UDINT" or "UINT32" or "DWORD" => uint.Parse(value, CultureInfo.InvariantCulture),
            "LINT" or "INT64" => long.Parse(value, CultureInfo.InvariantCulture),
            "ULINT" or "UINT64" or "LWORD" => ulong.Parse(value, CultureInfo.InvariantCulture),
            "REAL" => float.Parse(value, CultureInfo.InvariantCulture),
            "LREAL" => double.Parse(value, CultureInfo.InvariantCulture),
            "STRING" or "WSTRING" => value,
            _ when typeName.StartsWith("STRING(", StringComparison.Ordinal) ||
                   typeName.StartsWith("WSTRING(", StringComparison.Ordinal) => value,
            _ => value,
        };
    }
}
