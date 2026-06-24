using System.ComponentModel;
using System.Text.RegularExpressions;
using BeckhoffMcp.Server.Services;
using Microsoft.Extensions.Logging;
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
    private readonly Microsoft.Extensions.Logging.ILogger<SymbolTools> _log;
    public SymbolTools(AdsConnectionManager ads, Microsoft.Extensions.Logging.ILoggerFactory lf)
    {
        _ads = ads;
        _log = lf.CreateLogger<SymbolTools>();
    }

    // Symbol loading + caching lives on the singleton AdsConnectionManager:
    // MCP tool types are instantiated per call, so any cache here would be dead
    // state. The first load after a fresh session returns empty (the symbol
    // upload only completes across an invocation boundary), which is why
    // beckhoff_connect calls _ads.WarmupSymbols() to prime it beforehand.
    private Task<ISymbolCollection<ISymbol>> LoadSymbolsAsync(CancellationToken ct = default)
        => Task.FromResult(_ads.GetSymbols());

    private static ISymbol? FindSymbol(ISymbolCollection<ISymbol> symbols, string name)
    {
        // Array elements (name[n]) and pointer derefs (p^.member) are never
        // in the flat symbol list — skip the O(n) scan immediately.
        if (name.Contains('[') || name.Contains('^'))
            return null;
        foreach (var s in symbols)
            if (s.InstancePath.Equals(name, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    /// <summary>
    /// Reads a value by instance path that is NOT in the flat symbol list.
    /// Array elements ('arr[5]'), pointer/reference dereferences ('p^.field')
    /// and other nested member expressions are not cached as standalone
    /// symbols, so an exact-match lookup always misses them. The string overload
    /// of <see cref="IAdsSymbolicAccess.ReadValue(string)"/> asks the target to
    /// resolve the full expression (handles indexing and '^') and read it in one
    /// round-trip. Throws when the target cannot resolve the path.
    /// </summary>
    private static object ReadValueByPath(IAdsConnection conn, string instancePath)
        => ((IAdsSymbolicAccess)conn).ReadValue(instancePath);

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

    // Categories whose SubSymbols are structural members reachable WITHOUT a
    // runtime dereference — safe to recurse during a deep search.
    private static bool IsStructural(DataTypeCategory? cat) =>
        cat is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock
            or DataTypeCategory.Program or DataTypeCategory.Union;

    // Reference-like categories: their SubSymbols represent the TARGET's layout,
    // so walking them follows a pointer. The controller tree is a graph with
    // cycles (pTRK_CIf, In_pFbTRK, …) — recursing these without a guard loops
    // forever. Off by default; only entered when deref is explicitly requested.
    private static bool IsPointerLike(DataTypeCategory? cat) =>
        cat is DataTypeCategory.Pointer or DataTypeCategory.Reference
            or DataTypeCategory.Interface;

    /// <summary>
    /// Recursively walks the type tree under <paramref name="root"/> via lazy
    /// <c>SubSymbols</c>, collecting symbols whose InstancePath matches the
    /// regex. Finds nested members that are NOT in the published flat symbol
    /// table. Bounded by depth, result count, a node backstop and the token's
    /// timeout. Pointer/Reference/Interface members are treated as leaves
    /// (raw value, no descent) unless <paramref name="derefPointers"/> is set —
    /// this is the cycle guard, not just a perf knob.
    /// </summary>
    private static (List<ISymbol> hits, bool stoppedEarly) SearchTreeRecursive(
        IEnumerable<ISymbol> roots, Regex regex, int maxDepth, bool resolveArrays,
        int arrayLimit, bool derefPointers, int maxResults,
        CancellationToken ct)
    {
        const int MaxNodes = 2_000_000;   // runaway backstop; timeout is the real bound
        var hits = new List<ISymbol>();
        var seenHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = 0;
        var stoppedEarly = false;

        // True when we must stop walking — timeout, node cap or result cap.
        // On timeout/node-cap we return the PARTIAL hits collected so far
        // (graceful) rather than throwing and losing everything.
        bool Done()
        {
            if (hits.Count >= maxResults) return true;
            if (nodes >= MaxNodes || ct.IsCancellationRequested) { stoppedEarly = true; return true; }
            return false;
        }

        void Walk(ISymbol sym, int depth)
        {
            if (Done()) return;
            nodes++;

            try
            {
                if (regex.IsMatch(sym.InstancePath) && seenHits.Add(sym.InstancePath))
                    hits.Add(sym);
            }
            catch (RegexMatchTimeoutException) { /* skip on per-match timeout */ }

            if (depth >= maxDepth || Done()) return;

            var cat = sym.DataType?.Category;

            if (IsPointerLike(cat))
            {
                // Default: leaf. Cycle guard for the controller graph.
                if (!derefPointers || !visited.Add(sym.InstancePath)) return;
                foreach (var child in sym.SubSymbols)
                {
                    Walk(child, depth + 1);
                    if (Done()) return;
                }
                return;
            }

            if (cat == DataTypeCategory.Array)
            {
                if (!resolveArrays) return;   // arrays are leaves unless asked
                var taken = 0;
                foreach (var child in sym.SubSymbols)
                {
                    if (taken++ >= arrayLimit) break;
                    Walk(child, depth + 1);
                    if (Done()) return;
                }
                return;
            }

            if (IsStructural(cat))
            {
                foreach (var child in sym.SubSymbols)
                {
                    Walk(child, depth + 1);
                    if (Done()) return;
                }
            }
            // Primitive / String / Enum / unknown → leaf.
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
            if (Done()) break;
        }
        return (hits, stoppedEarly);
    }

    [McpServerTool(Name = "beckhoff_list_symbols"),
     Description("List PLC symbols. Use 'name_filter' for substring match (fast, simple) OR 'pattern' for full regex " +
                 "search (case-insensitive, e.g. '.*\\._stateString$', 'MAIN\\.motor\\..*'). 'parent_path' limits the regex " +
                 "scope to a sub-tree (e.g. 'MAIN'). 'include_array_elements' enables matching inside array elements " +
                 "(slower but covers e.g. 'arData[3].x'). Set 'recurse'=true to DEEP-search nested FB/struct members that " +
                 "are NOT in the published flat symbol list (e.g. 'fbCtrl.CIf.Job.strState') — requires 'parent_path'. " +
                 "Discovery only: returns paths/types; read the values afterwards with beckhoff_read_variables.")]
    public async Task<object> ListSymbols(
        [Description("Substring filter (case-insensitive). Use OR pattern, not both.")] string? name_filter = null,
        [Description("Full regex pattern. Overrides name_filter. Case-insensitive.")] string? pattern = null,
        [Description("Limit regex scope to children of this parent path (e.g. 'MAIN'). REQUIRED when recurse=true.")] string? parent_path = null,
        [Description("Maximum number of symbols to return (default 200).")] int limit = 200,
        [Description("Include array elements when scanning (slower; default false).")] bool include_array_elements = false,
        [Description("Timeout for the search in seconds (default 30).")] int timeout_seconds = 30,
        [Description("Deep-walk the type tree under parent_path to find nested members not in the flat list. Requires parent_path.")] bool recurse = false,
        [Description("Max recursion depth below parent_path (default 8). Only used with recurse=true.")] int max_depth = 8,
        [Description("Descend into array elements while recursing (default false). Only used with recurse=true.")] bool resolve_arrays = false,
        [Description("Max elements expanded per array when resolve_arrays=true (default 5).")] int array_element_limit = 5,
        [Description("Follow Pointer/Interface/Reference members into their target (default false = raw leaf). Cycle-bounded by max_depth. Only used with recurse=true.")] bool deref_pointers = false,
        [Description("ADS timeout in seconds for the resolve step when recurse=true (default 15).")] int deref_timeout_seconds = 15,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));

            // ---- Deep / recursive type-tree search -------------------------
            if (recurse)
            {
                if (string.IsNullOrWhiteSpace(parent_path))
                    return new { ok = false, error = "recurse=true requires a non-empty parent_path (cost bound — the full tree of all root symbols cannot be recursed)." };

                // Bound the resolve round-trip; the walk itself is local (type table).
                var conn = _ads.EnsureConnected();
                ApplyAdsTimeout(conn, deref_timeout_seconds);

                // Determine the start node(s). Prefer resolving parent_path to a
                // single symbol (FB/struct instance). If it is not a resolvable
                // data symbol (e.g. a PROGRAM like 'FastPRG_1'), derive the direct
                // child instances from the published flat symbols beneath it and
                // resolve each. This reaches controllers that live inside arrays
                // (e.g. 'FastPRG_1.fbMVR[0]') whose only published flat entries
                // are deeper sub-members ('…fbMVR[0].fbMover.MCTOPLC') — taking
                // the first path segment after the prefix (array index included)
                // recovers the instance node the controller's CIf hangs off.
                List<ISymbol> roots;
                var resolved = _ads.ResolveSymbol(parent_path!);
                if (resolved is not null)
                {
                    roots = new List<ISymbol> { resolved };
                }
                else
                {
                    var prefix = parent_path!.TrimEnd('.') + ".";
                    var flat = await LoadSymbolsAsync(ct).ConfigureAwait(false);

                    // Distinct first segment after the prefix (e.g. 'fbMVR[0]',
                    // 'fbTRM'); '.' separates members, '[i]' stays with its name.
                    var candidates = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in flat)
                    {
                        if (!s.InstancePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var rest = s.InstancePath.Substring(prefix.Length);
                        var seg = rest.Split('.')[0];
                        if (seg.Length == 0) continue;
                        var cand = prefix + seg;
                        if (seen.Add(cand)) candidates.Add(cand);
                    }

                    roots = new List<ISymbol>();
                    foreach (var cand in candidates)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var sym = _ads.ResolveSymbol(cand);
                        if (sym is not null) roots.Add(sym);
                    }
                    if (roots.Count == 0)
                        return new { ok = false, error = $"parent_path '{parent_path}' could not be resolved and no child instances beneath it could be resolved.", mode = "recurse" };
                }

                var regex = string.IsNullOrEmpty(pattern)
                    ? BuildRegex(string.IsNullOrEmpty(name_filter) ? ".*" : Regex.Escape(name_filter!))
                    : BuildRegex(pattern!);

                var (deep, stoppedEarly) = SearchTreeRecursive(roots, regex, Math.Max(1, max_depth), resolve_arrays,
                    Math.Max(1, array_element_limit), deref_pointers, limit, cts.Token);

                var deepList = deep.Select(s => new { name = s.InstancePath, type = s.TypeName, size = s.Size }).ToList();
                return new
                {
                    ok = true,
                    count = deepList.Count,
                    truncated = deepList.Count >= limit,
                    // True when the walk hit the time/node budget before covering
                    // the whole tree — results are partial. Narrow parent_path or
                    // lower max_depth, or use the g_a_pCtrlCIf pointer array for a
                    // complete controller sweep.
                    partial = stoppedEarly,
                    roots_walked = roots.Count,
                    mode = "recurse",
                    parent_path,
                    max_depth,
                    symbols = deepList,
                };
            }

            // ---- Flat search (unchanged) -----------------------------------
            var symbols = await LoadSymbolsAsync(ct).ConfigureAwait(false);
            var total = symbols.Count;
            IEnumerable<ISymbol> seq;

            if (!string.IsNullOrEmpty(pattern))
            {
                seq = SearchByRegex(symbols, BuildRegex(pattern), parent_path, limit, include_array_elements, cts.Token);
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

            return new
            {
                count = list.Count,
                total,
                truncated = list.Count == limit,
                mode = pattern != null ? "regex" : (name_filter != null ? "substring" : "all"),
                symbols = list,
                _diag = _ads.LastSymbolLoadDiag,
            };
        }
        catch (Exception ex)
        {
            return ErrInfo("list_symbols", ex);
        }
    }

    [McpServerTool(Name = "beckhoff_get_symbol_info"),
     Description("Get detailed information about a single PLC symbol by name.")]
    public async Task<object> GetSymbolInfo(
        [Description("Full symbol path, e.g. 'MAIN.fbCounter.iValue'")] string symbol_name)
    {
        try
        {
            var symbols = await LoadSymbolsAsync().ConfigureAwait(false);
            var symbol = FindSymbol(symbols, symbol_name);
            if (symbol is null)
                return new { name = symbol_name, ok = false, error = $"Symbol '{symbol_name}' not found" };

            var ads = symbol as Symbol;
            return new
            {
                name = symbol.InstancePath,
                type = symbol.TypeName,
                size = symbol.Size,
                index_group = ads?.IndexGroup,
                index_offset = ads?.IndexOffset,
                comment = symbol.Comment,
                ok = true,
            };
        }
        catch (Exception ex)
        {
            return ErrInfo(symbol_name, ex);
        }
    }

    // ADS error 0x745 (1861) = ClientSyncTimeOut: the ADS server on the
    // target did not respond in time. AdsException carries the error code
    // only in its message string (no typed property in this version).
    // Apply the caller's timeout to the ACTUAL ADS sync timeout. timeout_seconds
    // alone only bounds our CancellationToken; the AdsConnection has its own
    // internal sync timeout (default 5 s) that fires first as ClientSyncTimeOut
    // on a slow VPN link regardless of our token. Setting conn.Timeout makes the
    // configurable timeout real.
    private static void ApplyAdsTimeout(IAdsConnection conn, int timeoutSeconds)
    {
        if (conn is AdsConnection ac)
            ac.Timeout = Math.Max(1000, timeoutSeconds * 1000);
    }

    private static bool IsAdsTimeout(Exception ex) =>
        ex.Message.Contains("ClientSyncTimeOut") ||
        ex.Message.Contains("Timeout has elapsed") ||
        ex.Message.Contains("1861");

    // Threshold for typed materialisation. ReadValueAsync(ISymbol) builds a
    // deep typed object graph from the marshalled bytes; on a large/complex
    // symbol (e.g. a 240 KB FunctionBlock) the native marshaller can throw an
    // UNCATCHABLE StackOverflow/AccessViolation that kills the whole MCP
    // process — no managed try/catch can stop it (proven: the process died even
    // with the value serialisation moved inside the tool's try block). The only
    // safe defence is to NOT materialise such values: above this size, or for
    // FunctionBlock/Program symbols, we read RAW bytes (IndexGroup/Offset/Size)
    // and return them base64-encoded, which never materialises a typed graph.
    // Small scalars/strings/structs stay typed for convenience.
    private const int DefaultMaxReadBytes = 4096;

    /// <summary>
    /// True when a typed read would risk the marshaller — read raw bytes instead.
    /// </summary>
    private static bool ShouldReadRaw(ISymbol sym, int maxTypedBytes)
    {
        if (sym.Size > maxTypedBytes) return true;
        var cat = sym.DataType?.Category;
        return cat is DataTypeCategory.FunctionBlock or DataTypeCategory.Program;
    }

    /// <summary>
    /// Reads a symbol's raw bytes via IndexGroup/IndexOffset/Size and returns
    /// them base64-encoded. Never materialises a typed value graph, so it cannot
    /// trigger the marshaller crash that a typed read of a large/complex symbol
    /// can. Returns null if the symbol carries no IndexGroup/Offset addressing.
    /// </summary>
    private static async Task<(object? payload, bool ok)> ReadRawBytesAsync(IAdsConnection conn, ISymbol sym, CancellationToken ct)
    {
        if (sym is not Symbol s) return (null, false);
        var buf = new byte[Math.Max(0, sym.Size)];
        var rr = await conn.ReadAsync(s.IndexGroup, s.IndexOffset, buf.AsMemory(), ct).ConfigureAwait(false);
        if (!rr.Succeeded)
            return (new { name = sym.InstancePath, ok = false, error = rr.ErrorCode.ToString() }, false);
        return (new
        {
            name = sym.InstancePath,
            type = sym.TypeName,
            size = sym.Size,
            encoding = "base64",
            value_base64 = Convert.ToBase64String(buf, 0, rr.ReadBytes),
            ok = true,
            note = "Large/complex type returned as raw bytes — typed materialisation of this " +
                   "symbol can crash the ADS value marshaller. Decode the bytes, or read a " +
                   "specific member/array element (those resolve server-side and stay safe).",
        }, true);
    }

    // JSON depth must comfortably exceed deep PLC struct nesting; the default
    // System.Text.Json limit (64) is easily exceeded by a large FB graph.
    private static readonly System.Text.Json.JsonSerializerOptions SafeJsonOptions =
        new() { MaxDepth = 256 };

    /// <summary>
    /// Serialises a marshalled PLC value to a plain JsonElement INSIDE the
    /// caller's try block. This moves any serialisation failure (max depth,
    /// unsupported type, cycles) into our own exception handling so it is
    /// reported as a structured error instead of escaping to the MCP framework
    /// (where it surfaces only as an opaque "An error occurred invoking").
    /// </summary>
    private static System.Text.Json.JsonElement ToJsonSafe(object? value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, SafeJsonOptions);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Structured error payload with full exception detail for debugging.</summary>
    private static object ErrInfo(string name, Exception ex) => new
    {
        name,
        ok = false,
        error = ex.Message,
        exceptionType = ex.GetType().FullName,
        inner = ex.InnerException?.Message,
        innerType = ex.InnerException?.GetType().FullName,
        stackTrace = ex.StackTrace,
    };


    [McpServerTool(Name = "beckhoff_read_variable"),
     Description("Read a single PLC variable by symbolic name.")]
    public async Task<object> ReadVariable(
        [Description("Full symbol path, e.g. 'MAIN.fbCounter.iValue'")] string symbol_name,
        [Description("Per-read timeout in seconds (default 15). Increase for slow VPN links.")] int timeout_seconds = 15,
        [Description("Symbols larger than this (or FunctionBlock/Program types) are returned as raw base64 bytes instead of a typed value, because typed materialisation of a large/complex symbol can crash the ADS marshaller. Default 4096.")] int max_bytes = DefaultMaxReadBytes,
        CancellationToken ct = default)
    {
        var conn = _ads.EnsureConnected();
        ApplyAdsTimeout(conn, timeout_seconds);
        var symbols = await LoadSymbolsAsync(ct).ConfigureAwait(false);
        var symbol = FindSymbol(symbols, symbol_name);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));

            if (symbol is null)
            {
                // Not in the flat symbol list — resolve+read the instance path
                // (array element / pointer-deref / nested member) server-side.
                // ReadValue(string) returns raw bytes for complex types (no deep
                // typed materialisation), so this path does not risk the crash.
                // ReadValueByPath is synchronous; wrap in Task.Run so WaitAsync
                // can enforce the timeout without blocking the thread pool.
                var byPath = await Task.Run(() => ReadValueByPath(conn, symbol_name), cts.Token)
                    .WaitAsync(cts.Token);
                return new { name = symbol_name, type = (string?)null, value = ToJsonSafe(byPath), ok = true };
            }

            // Large/complex symbols: read raw bytes instead of materialising a
            // typed graph (which can crash the marshaller and kill the process).
            if (ShouldReadRaw(symbol, max_bytes))
            {
                var (raw, _) = await ReadRawBytesAsync(conn, symbol, cts.Token);
                if (raw is not null) return raw;
            }

            var result = await conn.ReadValueAsync(symbol, cts.Token);
            if (result.ErrorCode != AdsErrorCode.NoError)
                return new { name = symbol.InstancePath, ok = false, error = result.ErrorCode.ToString() };
            return new { name = symbol.InstancePath, type = symbol.TypeName, value = ToJsonSafe(result.Value), ok = true };
        }
        catch (Exception ex)
        {
            // A timeout may have left a partial AMS frame in the TCP buffer.
            // Invalidate the main connection so the next call gets a clean session.
            if (IsAdsTimeout(ex) || ex is OperationCanceledException)
                _ads.InvalidateConnection();
            return ErrInfo(symbol_name, ex);
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
        [Description("Timeout for regex search AND per-read in seconds (default 30).")] int timeout_seconds = 30,
        [Description("Symbols larger than this (or FunctionBlock/Program types) are returned as raw base64 bytes instead of a typed value, because typed materialisation of a large/complex symbol can crash the ADS marshaller. Default 4096.")] int max_bytes = DefaultMaxReadBytes,
        CancellationToken ct = default)
    {
        var symbols = await LoadSymbolsAsync(ct).ConfigureAwait(false);
        var results = new List<object>();
        var successCount = 0;

        async Task ReadOne(ISymbol symbol)
        {
            try
            {
                // Re-acquire the connection per read: a previous read's timeout
                // calls InvalidateConnection(), which disposes the session. A
                // connection captured once and reused would then throw
                // ObjectDisposedException for every subsequent read in the batch
                // (one timeout poisoning the whole batch). EnsureConnected()
                // rebuilds a fresh session when the prior one was invalidated.
                var conn = _ads.EnsureConnected();
                ApplyAdsTimeout(conn, timeout_seconds);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));

                // Large/complex symbols: raw bytes, never typed materialisation.
                if (ShouldReadRaw(symbol, max_bytes))
                {
                    var (raw, rawOk) = await ReadRawBytesAsync(conn, symbol, cts.Token);
                    if (raw is not null)
                    {
                        results.Add(raw);
                        if (rawOk) successCount++;
                        return;
                    }
                }

                var r = await conn.ReadValueAsync(symbol, cts.Token);
                if (r.ErrorCode == AdsErrorCode.NoError)
                {
                    results.Add(new
                    {
                        name = symbol.InstancePath,
                        type = symbol.TypeName,
                        category = symbol.DataType?.Category.ToString(),
                        value = ToJsonSafe(r.Value),
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
                if (IsAdsTimeout(ex) || ex is OperationCanceledException)
                    _ads.InvalidateConnection();
                results.Add(ErrInfo(symbol.InstancePath, ex));
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
            if (symbol is not null)
            {
                await ReadOne(symbol);
                continue;
            }
            // Not in the flat symbol list — resolve+read the instance path
            // (array element / pointer-deref / nested member) server-side.
            try
            {
                // Re-acquire per read (see ReadOne) so a prior timeout's
                // InvalidateConnection() does not leave us a disposed session.
                var conn = _ads.EnsureConnected();
                ApplyAdsTimeout(conn, timeout_seconds);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout_seconds)));
                // ReadValue(string) returns raw bytes for complex types — safe.
                var byPath = await Task.Run(() => ReadValueByPath(conn, name), cts.Token)
                    .WaitAsync(cts.Token);
                results.Add(new { name, value = ToJsonSafe(byPath), ok = true });
                successCount++;
            }
            catch (Exception ex)
            {
                if (IsAdsTimeout(ex) || ex is OperationCanceledException)
                    _ads.InvalidateConnection();
                results.Add(ErrInfo(name, ex));
            }
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
