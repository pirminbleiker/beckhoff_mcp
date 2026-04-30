using System.ComponentModel;
using System.Text.RegularExpressions;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private static readonly TimeSpan RegexPerMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly AdsConnectionManager _ads;
    public SymbolTools(AdsConnectionManager ads) => _ads = ads;

    private static readonly SymbolLoaderSettings FlatSettings =
        new(SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred);

    private ISymbolCollection<ISymbol> LoadSymbols()
    {
        var conn = _ads.EnsureConnected();
        var loader = SymbolLoaderFactory.Create(conn, FlatSettings);
        return loader.Symbols;
    }

    private static ISymbol? FindSymbol(ISymbolCollection<ISymbol> symbols, string name)
    {
        foreach (var s in symbols)
            if (s.InstancePath.Equals(name, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private static Regex BuildRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexPerMatchTimeout);

    /// <summary>
    /// Iterates symbols (recursively when 'recurse' is true) and returns the
    /// ones whose InstancePath matches the regex. parentPath limits scope.
    /// </summary>
    private static List<ISymbol> SearchByRegex(
        ISymbolCollection<ISymbol> symbols, Regex regex, string? parentPath,
        int maxResults, bool includeArrayElements, CancellationToken ct)
    {
        // Flat-mode loader exposes a flat list; SubSymbols are not populated.
        // We honour parent_path as a path-prefix filter on InstancePath, which
        // gives the same scoping effect without requiring a tree-mode loader.
        var prefix = string.IsNullOrEmpty(parentPath) ? null : parentPath + ".";

        var hits = new List<ISymbol>();
        foreach (var s in symbols)
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= maxResults) break;

            if (prefix is not null && !s.InstancePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (regex.IsMatch(s.InstancePath))
                    hits.Add(s);
            }
            catch (RegexMatchTimeoutException) { /* skip on per-match timeout */ }
        }

        // Optionally also expand into array elements
        if (includeArrayElements && hits.Count < maxResults)
        {
            // Build child iterator from current hits using SymbolIterator on their
            // SubSymbols (works regardless of Flat/VirtualTree because subsymbols
            // are lazy-loaded by the loader on demand).
            var rootSource = hits.SelectMany(h => h.SubSymbols.Cast<ISymbol>());
            var iterator = new SymbolIterator(
                rootSource, recurse: true, SymbolIterationMask.All,
                s => { ct.ThrowIfCancellationRequested(); try { return regex.IsMatch(s.InstancePath); } catch (RegexMatchTimeoutException) { return false; } },
                _ => { ct.ThrowIfCancellationRequested(); return true; });
            foreach (var sym in iterator)
            {
                if (hits.Count >= maxResults) break;
                hits.Add(sym);
            }
        }

        return hits;
    }

    [McpServerTool(Name = "beckhoff_list_symbols"),
     Description("List PLC symbols. Use 'name_filter' for substring match (fast, simple) OR 'pattern' for full regex " +
                 "search (case-insensitive, e.g. '.*\\._stateString$', 'MAIN\\.motor\\..*'). 'parent_path' limits the regex " +
                 "scope to a sub-tree (e.g. 'MAIN'). 'include_array_elements' enables matching inside array elements " +
                 "(slower but covers e.g. 'arData[3].x').")]
    public Task<object> ListSymbols(
        [Description("Substring filter (case-insensitive). Use OR pattern, not both.")] string? name_filter = null,
        [Description("Full regex pattern. Overrides name_filter. Case-insensitive.")] string? pattern = null,
        [Description("Limit regex scope to children of this parent path (e.g. 'MAIN').")] string? parent_path = null,
        [Description("Maximum number of symbols to return (default 200).")] int limit = 200,
        [Description("Include array elements when scanning (slower; default false).")] bool include_array_elements = false,
        [Description("Timeout for regex search in seconds (default 30).")] int timeout_seconds = 30,
        CancellationToken ct = default)
    {
        var symbols = LoadSymbols();
        var total = symbols.Count;
        IEnumerable<ISymbol> seq;

        if (!string.IsNullOrEmpty(pattern))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));
            try
            {
                seq = SearchByRegex(symbols, BuildRegex(pattern), parent_path, limit, include_array_elements, cts.Token);
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(new { ok = false, error = ex.Message });
            }
        }
        else
        {
            seq = symbols;
            if (!string.IsNullOrEmpty(name_filter))
                seq = seq.Where(s => s.InstancePath.Contains(name_filter, StringComparison.OrdinalIgnoreCase));
        }

        var list = seq.Take(limit).Select(s => new
        {
            name = s.InstancePath,
            type = s.TypeName,
            size = s.Size,
        }).ToList();

        return Task.FromResult<object>(new
        {
            count = list.Count,
            total,
            truncated = list.Count == limit,
            mode = pattern != null ? "regex" : (name_filter != null ? "substring" : "all"),
            symbols = list,
        });
    }

    [McpServerTool(Name = "beckhoff_get_symbol_info"),
     Description("Get detailed information about a single PLC symbol by name.")]
    public Task<object> GetSymbolInfo(
        [Description("Full symbol path, e.g. 'MAIN.fbCounter.iValue'")] string symbol_name)
    {
        var symbols = LoadSymbols();
        var symbol = FindSymbol(symbols, symbol_name)
            ?? throw new InvalidOperationException($"Symbol '{symbol_name}' not found");

        var ads = symbol as Symbol;
        return Task.FromResult<object>(new
        {
            name = symbol.InstancePath,
            type = symbol.TypeName,
            size = symbol.Size,
            index_group = ads?.IndexGroup,
            index_offset = ads?.IndexOffset,
            comment = symbol.Comment,
        });
    }

    [McpServerTool(Name = "beckhoff_read_variable"),
     Description("Read a single PLC variable by symbolic name.")]
    public async Task<object> ReadVariable(
        [Description("Full symbol path, e.g. 'MAIN.fbCounter.iValue'")] string symbol_name,
        CancellationToken ct = default)
    {
        var symbols = LoadSymbols();
        var symbol = FindSymbol(symbols, symbol_name);
        if (symbol is null)
            return new { name = symbol_name, ok = false, error = "not_found" };

        try
        {
            var conn = _ads.EnsureConnected();
            var result = await conn.ReadValueAsync(symbol, ct);
            if (result.ErrorCode != AdsErrorCode.NoError)
                return new { name = symbol.InstancePath, ok = false, error = result.ErrorCode.ToString() };
            return new { name = symbol.InstancePath, type = symbol.TypeName, value = result.Value, ok = true };
        }
        catch (Exception ex)
        {
            return new { name = symbol_name, ok = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "beckhoff_read_variables"),
     Description("Read multiple PLC variables. Two modes: " +
                 "(1) explicit list via 'symbol_names'; " +
                 "(2) regex 'pattern' to find symbols and read each match in one operation. " +
                 "Use 'parent_path' to scope a regex search. Both modes return per-item results.")]
    public async Task<object> ReadVariables(
        [Description("Explicit list of full symbol paths. Ignored if 'pattern' is set.")] string[]? symbol_names = null,
        [Description("Regex pattern (case-insensitive) to find and read all matching symbols.")] string? pattern = null,
        [Description("Limit regex scope to children of this parent path (e.g. 'MAIN').")] string? parent_path = null,
        [Description("Maximum number of regex matches to read (default 100).")] int max_results = 100,
        [Description("Include array elements when scanning (default false).")] bool include_array_elements = false,
        [Description("Timeout for regex search in seconds (default 30).")] int timeout_seconds = 30,
        CancellationToken ct = default)
    {
        var symbols = LoadSymbols();
        var conn = _ads.EnsureConnected();
        var results = new List<object>();
        var successCount = 0;

        async Task ReadOne(ISymbol symbol)
        {
            try
            {
                var r = await conn.ReadValueAsync(symbol, ct);
                if (r.ErrorCode == AdsErrorCode.NoError)
                {
                    results.Add(new
                    {
                        name = symbol.InstancePath,
                        type = symbol.TypeName,
                        category = symbol.DataType?.Category.ToString(),
                        value = r.Value,
                        ok = true,
                    });
                    successCount++;
                }
                else
                {
                    results.Add(new { name = symbol.InstancePath, ok = false, error = r.ErrorCode.ToString() });
                }
            }
            catch (Exception ex)
            {
                results.Add(new { name = symbol.InstancePath, ok = false, error = ex.Message });
            }
        }

        if (!string.IsNullOrEmpty(pattern))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));
            List<ISymbol> matches;
            try
            {
                matches = SearchByRegex(symbols, BuildRegex(pattern), parent_path,
                    max_results, include_array_elements, cts.Token);
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message, mode = "regex" };
            }
            foreach (var sym in matches) await ReadOne(sym);
            return new
            {
                ok = successCount == matches.Count,
                mode = "regex",
                pattern,
                matched_count = matches.Count,
                success_count = successCount,
                results,
            };
        }

        if (symbol_names is null || symbol_names.Length == 0)
            return new { ok = false, error = "Either 'symbol_names' or 'pattern' must be provided." };

        foreach (var name in symbol_names)
        {
            var symbol = FindSymbol(symbols, name);
            if (symbol is null)
            {
                results.Add(new { name, ok = false, error = "not_found" });
                continue;
            }
            await ReadOne(symbol);
        }

        return new
        {
            ok = successCount == symbol_names.Length,
            mode = "explicit",
            requested_count = symbol_names.Length,
            success_count = successCount,
            results,
        };
    }
}
