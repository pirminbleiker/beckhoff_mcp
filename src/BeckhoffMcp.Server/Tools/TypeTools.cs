using System.ComponentModel;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class TypeTools
{
    private static readonly SymbolLoaderSettings FlatSettings = new(
        SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred);

    private readonly AdsConnectionManager _ads;
    public TypeTools(AdsConnectionManager ads) => _ads = ads;

    [McpServerTool(Name = "beckhoff_get_type_info"),
     Description("Deep type introspection. Returns members (struct/FB/interface), RPC methods, base type, " +
                 "interface implementations, and pointer/reference target type. Accepts either a symbol path " +
                 "(e.g. 'MAIN.fbMotor') or a type name (e.g. 'FB_Motor', 'I_Axis').")]
    public async Task<object> GetTypeInfo(
        [Description("Symbol path or type name to inspect.")] string type_or_symbol_path,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);

            IDataType? dataType = null;
            string? sourceSymbolPath = null;

            // Try as symbol path first
            foreach (var s in loader.Symbols)
            {
                if (s.InstancePath.Equals(type_or_symbol_path, StringComparison.OrdinalIgnoreCase))
                {
                    dataType = s.DataType;
                    sourceSymbolPath = s.InstancePath;
                    break;
                }
            }

            // Fallback: search type name in DataTypes collection
            if (dataType is null)
            {
                foreach (var dt in loader.DataTypes)
                {
                    if (string.Equals(dt.Name, type_or_symbol_path, StringComparison.OrdinalIgnoreCase))
                    {
                        dataType = dt;
                        break;
                    }
                }
            }

            if (dataType is null)
                return new { ok = false, error = $"Type or symbol not found: '{type_or_symbol_path}'" };

            return new
            {
                ok = true,
                source_symbol_path = sourceSymbolPath,
                type_name = dataType.Name,
                category = dataType.Category.ToString(),
                byte_size = dataType.ByteSize,
                comment = string.IsNullOrEmpty(dataType.Comment) ? null : dataType.Comment,
                base_type = ExtractBaseType(dataType),
                interface_implementations = ExtractInterfaceImplementations(dataType),
                referenced_type = ExtractReferencedType(dataType),
                members = ExtractMembers(dataType),
                rpc_methods = ExtractRpcMethods(dataType),
                enum_values = ExtractEnumValues(dataType),
                array_info = ExtractArrayInfo(dataType),
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static string? ExtractBaseType(IDataType dt)
    {
        // IInterfaceType derives from IStructType in Beckhoff's API, so a
        // single test on IStructType covers both.
        if (dt is IStructType s && s.BaseType is not null) return s.BaseType.Name;
        return null;
    }

    private static List<string>? ExtractInterfaceImplementations(IDataType dt)
    {
        try
        {
            if (dt is IStructType s)
            {
                var list = s.InterfaceImplementationNames?.ToList();
                return list is { Count: > 0 } ? list : null;
            }
            return null;
        }
        catch { return null; }
    }

    private static string? ExtractReferencedType(IDataType dt) => dt switch
    {
        IPointerType p => p.ReferencedType?.Name,
        IReferenceType r => r.ReferencedType?.Name,
        _ => null,
    };

    private static List<object>? ExtractMembers(IDataType dt)
    {
        IEnumerable<IMember>? members = dt is IStructType s ? s.Members : null;
        if (members is null) return null;
        var list = members.Select(m => (object)new
        {
            name = m.InstanceName,
            type = m.DataType?.Name,
            size = m.ByteSize,
            comment = string.IsNullOrEmpty(m.Comment) ? null : m.Comment,
        }).ToList();
        return list.Count > 0 ? list : null;
    }

    private static List<object>? ExtractRpcMethods(IDataType dt)
    {
        if (dt is not IRpcCallableType rt || rt.RpcMethods.Count == 0) return null;
        return rt.RpcMethods.Select(m => (object)new
        {
            name = m.Name,
            return_type = m.ReturnType,
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
    }

    private static List<object>? ExtractEnumValues(IDataType dt)
    {
        if (dt is not IEnumType et || et.EnumValues.Count == 0) return null;
        return et.EnumValues.Select(v => (object)new
        {
            name = v.Name,
            value = v.Value,
            comment = string.IsNullOrEmpty(v.Comment) ? null : v.Comment,
        }).ToList();
    }

    private static object? ExtractArrayInfo(IDataType dt)
    {
        if (dt is not IArrayType arr) return null;
        return new
        {
            element_type = arr.ElementType?.Name,
            dimensions = arr.Dimensions.Select(d => new { lower = d.LowerBound, upper = d.LowerBound + d.ElementCount - 1, length = d.ElementCount }).ToList(),
        };
    }

    [McpServerTool(Name = "beckhoff_dereference"),
     Description("Read the target of a POINTER, REFERENCE, or INTERFACE variable. Returns the dereferenced value " +
                 "or null if the pointer/reference is unassigned.")]
    public async Task<object> Dereference(
        [Description("Symbol path of a pointer/reference/interface, e.g. 'MAIN.pCounter'.")] string symbol_path,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
            ISymbol? symbol = null;
            foreach (var s in loader.Symbols)
                if (s.InstancePath.Equals(symbol_path, StringComparison.OrdinalIgnoreCase))
                    { symbol = s; break; }
            if (symbol is null)
                return new { ok = false, error = $"Symbol not found: '{symbol_path}'" };

            var category = symbol.DataType?.Category ?? DataTypeCategory.Primitive;

            if (category == DataTypeCategory.Pointer && symbol is IPointerInstance ptr)
            {
                var refSym = ptr.Reference;
                if (refSym is null)
                    return new { ok = true, original_path = symbol_path, is_null = true, category = "Pointer" };
                var v = ((IValueSymbol)refSym).ReadValue();
                return new
                {
                    ok = true,
                    original_path = symbol_path,
                    dereferenced_path = refSym.InstancePath,
                    type = refSym.TypeName,
                    category = "Pointer",
                    is_null = false,
                    value = v,
                };
            }

            // Reference / Interface: try ReadValue, fall back to SubSymbols
            try
            {
                var v = ((IValueSymbol)symbol).ReadValue();
                if (v is null && (category == DataTypeCategory.Reference || category == DataTypeCategory.Interface))
                    return new { ok = true, original_path = symbol_path, is_null = true, category = category.ToString() };
                return new
                {
                    ok = true,
                    original_path = symbol_path,
                    type = symbol.TypeName,
                    category = category.ToString(),
                    is_null = false,
                    value = v,
                };
            }
            catch
            {
                if (symbol.SubSymbols.Count > 0)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (ISymbol child in symbol.SubSymbols)
                    {
                        try { dict[child.InstanceName] = ((IValueSymbol)child).ReadValue(); }
                        catch { dict[child.InstanceName] = null; }
                    }
                    return new
                    {
                        ok = true,
                        original_path = symbol_path,
                        type = symbol.TypeName,
                        category = category.ToString(),
                        is_null = false,
                        value = dict,
                    };
                }
                return new { ok = false, error = "Could not dereference and no sub-symbols available." };
            }
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }
}
