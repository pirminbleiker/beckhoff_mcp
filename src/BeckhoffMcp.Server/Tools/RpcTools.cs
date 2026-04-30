using System.ComponentModel;
using System.Globalization;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class RpcTools
{
    private static readonly SymbolLoaderSettings FlatSettings = new(
        SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred);

    private readonly AdsConnectionManager _ads;
    public RpcTools(AdsConnectionManager ads) => _ads = ads;

    private static ISymbol? FindSymbol(ISymbolCollection<ISymbol> symbols, string name)
    {
        foreach (var s in symbols)
            if (s.InstancePath.Equals(name, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    [McpServerTool(Name = "beckhoff_get_rpc_methods"),
     Description("List RPC methods available on a function-block instance or its type. " +
                 "Methods must be marked {attribute 'TcRpcEnable'} in PLC code. Returns method names, " +
                 "return types, and parameter signatures (name, type, direction, size). Use beckhoff_invoke_rpc to call them.")]
    public async Task<object> GetRpcMethods(
        [Description("Symbol path of a function-block instance, e.g. 'MAIN.fbController'.")] string symbol_path,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
            var symbol = FindSymbol(loader.Symbols, symbol_path);
            if (symbol is null)
                return new { ok = false, error = $"Symbol not found: '{symbol_path}'" };

            IEnumerable<IRpcMethod>? methods = null;
            if (symbol is IRpcCallableInstance ri && ri.RpcMethods.Count > 0)
                methods = ri.RpcMethods;
            else if (symbol.DataType is IRpcCallableType rt && rt.RpcMethods.Count > 0)
                methods = rt.RpcMethods;

            if (methods is null)
                return new
                {
                    ok = false,
                    symbol = symbol.InstancePath,
                    type = symbol.TypeName,
                    error = "No RPC methods. Mark methods with {attribute 'TcRpcEnable'} in PLC code.",
                };

            var list = methods.Select(m => new
            {
                name = m.Name,
                return_type = m.ReturnType,
                is_void = string.IsNullOrEmpty(m.ReturnType) ||
                          string.Equals(m.ReturnType, "VOID", StringComparison.OrdinalIgnoreCase),
                comment = string.IsNullOrEmpty(m.Comment) ? null : m.Comment,
                parameters = m.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.TypeName,
                    size = p.Size,
                    is_input = p.ParameterFlags.HasFlag(MethodParamFlags.In),
                    is_output = p.ParameterFlags.HasFlag(MethodParamFlags.Out),
                }).ToList(),
            }).ToList();

            return new
            {
                ok = true,
                symbol = symbol.InstancePath,
                type = symbol.TypeName,
                count = list.Count,
                methods = list,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public sealed record RpcParam(string Name, string Value);

    [McpServerTool(Name = "beckhoff_invoke_rpc"),
     Description("Invoke an RPC method on a PLC function-block instance. Returns the method's return value plus any " +
                 "OUT parameters. Discover methods first with beckhoff_get_rpc_methods. Input parameter values are passed " +
                 "as strings and auto-converted to the method's parameter types.")]
    public async Task<object> InvokeRpc(
        [Description("Symbol path of the function-block instance, e.g. 'MAIN.fbController'.")] string symbol_path,
        [Description("Method name to call, e.g. 'M_Add' or 'M_Reset'.")] string method_name,
        [Description("Input parameters: array of {name, value}. Omit for parameterless methods.")] RpcParam[]? parameters = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
            var symbol = FindSymbol(loader.Symbols, symbol_path);
            if (symbol is null)
                return new { ok = false, error = $"Symbol not found: '{symbol_path}'" };

            // Locate method
            IRpcMethod? method = null;
            if (symbol is IRpcCallableInstance ri)
                method = ri.RpcMethods.FirstOrDefault(m =>
                    string.Equals(m.Name, method_name, StringComparison.OrdinalIgnoreCase));
            if (method is null && symbol.DataType is IRpcCallableType rt)
                method = rt.RpcMethods.FirstOrDefault(m =>
                    string.Equals(m.Name, method_name, StringComparison.OrdinalIgnoreCase));

            if (method is null)
            {
                var avail = new List<string>();
                if (symbol is IRpcCallableInstance ri2)
                    foreach (var m in ri2.RpcMethods) avail.Add(m.Name);
                else if (symbol.DataType is IRpcCallableType rt2)
                    foreach (var m in rt2.RpcMethods) avail.Add(m.Name);
                return new
                {
                    ok = false,
                    error = $"RPC method '{method_name}' not found on '{symbol_path}'.",
                    available_methods = avail,
                };
            }

            var inputs = BuildInputParams(method, parameters);

            object? returnValue;
            object[]? outParams;

            if (symbol is IRpcCallableInstance rpcInstance)
            {
                returnValue = rpcInstance.InvokeRpcMethod(method.Name, inputs, out outParams);
            }
            else
            {
                // Fallback: connection-level RPC by symbol name
                returnValue = conn.InvokeRpcMethod(symbol.InstancePath, method.Name, inputs, out outParams);
            }

            // Map out-parameters back to names
            var outList = new List<object>();
            if (outParams is { Length: > 0 })
            {
                int outIdx = 0;
                foreach (var p in method.Parameters)
                {
                    if (p.ParameterFlags.HasFlag(MethodParamFlags.Out) && outIdx < outParams.Length)
                    {
                        outList.Add(new
                        {
                            name = p.Name,
                            type = p.TypeName,
                            value = outParams[outIdx]?.ToString() ?? "",
                        });
                        outIdx++;
                    }
                }
            }

            return new
            {
                ok = true,
                symbol = symbol.InstancePath,
                method = method.Name,
                return_type = method.ReturnType,
                return_value = returnValue,
                out_parameters = outList,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static object[] BuildInputParams(IRpcMethod method, RpcParam[]? supplied)
    {
        var inputDefs = method.Parameters.Where(p => p.ParameterFlags.HasFlag(MethodParamFlags.In)).ToList();
        if (inputDefs.Count == 0) return Array.Empty<object>();

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (supplied is not null)
            foreach (var p in supplied) byName[p.Name] = p.Value;

        var values = new object[inputDefs.Count];
        for (var i = 0; i < inputDefs.Count; i++)
        {
            var def = inputDefs[i];
            if (!byName.TryGetValue(def.Name, out var s))
                throw new ArgumentException($"Missing input parameter: '{def.Name}' (type {def.TypeName})");
            values[i] = ParseRpcValue(s, def.TypeName);
        }
        return values;
    }

    private static object ParseRpcValue(string value, string typeName)
    {
        var upper = typeName.ToUpperInvariant();
        return upper switch
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
            _ when upper.StartsWith("STRING(", StringComparison.Ordinal) ||
                   upper.StartsWith("WSTRING(", StringComparison.Ordinal) => value,
            _ => value,
        };
    }
}
