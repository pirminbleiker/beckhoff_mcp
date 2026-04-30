using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Manages subscription-based variable trace sessions using ADS notifications.
/// Each StartTrace returns a session id; data can be polled or streamed via GetTraceData.
/// Sessions auto-stop after maxDurationMs and are reaped after a TTL of inactivity.
/// </summary>
public sealed class TraceService : IDisposable
{
    private static readonly SymbolLoaderSettings FlatSettings = new(
        SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred);
    private static readonly TimeSpan RegexPerMatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleSessionTtl = TimeSpan.FromMinutes(5);
    private const int ReaperIntervalMs = 60_000;

    private readonly AdsConnectionManager _ads;
    private readonly ILogger<TraceService> _log;
    private readonly ConcurrentDictionary<string, TraceSession> _sessions = new();
    private int _counter;
    private Timer? _reaper;
    private bool _disposed;

    public TraceService(AdsConnectionManager ads, ILogger<TraceService> log)
    {
        _ads = ads;
        _log = log;
    }

    public sealed class StartResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? TraceId { get; init; }
        public List<TracedVariable> Variables { get; init; } = new();
        public int MaxDurationMs { get; init; }
    }

    public sealed class GetResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? TraceId { get; init; }
        public bool IsRunning { get; init; }
        public TraceSessionInfo? SessionInfo { get; init; }
        public List<TracedVariable>? Variables { get; init; }
        public List<TraceEvent>? Events { get; init; }
        public List<TraceVariableSummary>? Summary { get; init; }
        public string? Csv { get; init; }
    }

    public StartResult StartTrace(
        string? paths, string? pattern, string? parentPath,
        string mode, int cycleTimeMs, int maxDurationMs,
        int maxEvents, int maxSubscriptions, CancellationToken ct)
    {
        try
        {
            var conn = _ads.EnsureConnected();
            var loader = SymbolLoaderFactory.Create(conn, FlatSettings);

            var resolved = Resolve(loader.Symbols, paths, pattern, parentPath, maxSubscriptions, ct);
            if (resolved.Count == 0)
                return new StartResult { Ok = false, Error = "No symbols matched paths/pattern." };

            var traceId = $"trace-{Interlocked.Increment(ref _counter):D4}";
            var transMode = mode.Equals("cyclic", StringComparison.OrdinalIgnoreCase)
                ? AdsTransMode.Cyclic : AdsTransMode.OnChange;
            var session = new TraceSession(traceId, maxEvents, maxDurationMs);

            foreach (var sym in resolved)
            {
                var path = sym.InstancePath;
                var traced = new TracedVariable
                {
                    Path = path,
                    DataType = sym.DataType?.Name,
                    Category = ClassifyVariable(sym),
                    Size = sym.Size,
                };
                session.Variables.Add(traced);

                try
                {
                    var initial = ((IValueSymbol)sym).ReadValue();
                    session.LastValues[path] = initial;
                }
                catch { session.LastValues[path] = null; }

                var valSym = (IValueSymbol)sym;
                valSym.NotificationSettings = new NotificationSettings(transMode, cycleTimeMs, 0);

                EventHandler<ValueChangedEventArgs> handler = (_, e) => OnValueChanged(session, path, e);
                valSym.ValueChanged += handler;
                session.Subscriptions[path] = (valSym, handler);
            }

            // Initial snapshot as event 0
            var initialChanges = new Dictionary<string, object?>();
            foreach (var (k, v) in session.LastValues) initialChanges[k] = v;
            if (initialChanges.Count > 0)
                session.Events.Enqueue(new TraceEvent { TimestampMs = 0, Changes = initialChanges });

            session.IsRunning = true;
            session.AutoStopTimer = new Timer(_ => AutoStop(traceId), null, maxDurationMs, Timeout.Infinite);
            _sessions[traceId] = session;
            _reaper ??= new Timer(_ => Reap(), null, ReaperIntervalMs, ReaperIntervalMs);

            _log.LogInformation("Trace {TraceId} started: {Count} vars mode={Mode} maxDur={D}ms",
                traceId, session.Variables.Count, mode, maxDurationMs);

            return new StartResult
            {
                Ok = true,
                TraceId = traceId,
                Variables = session.Variables.ToList(),
                MaxDurationMs = maxDurationMs,
            };
        }
        catch (Exception ex)
        {
            return new StartResult { Ok = false, Error = ex.Message };
        }
    }

    public GetResult GetTraceData(string traceId, string format, int? sinceMs)
    {
        if (!_sessions.TryGetValue(traceId, out var session))
            return new GetResult { Ok = false, Error = $"Trace not found: '{traceId}'" };

        session.LastAccessUtc = DateTime.UtcNow;
        var events = session.Events.ToArray();
        if (sinceMs.HasValue) events = events.Where(e => e.TimestampMs > sinceMs.Value).ToArray();

        var info = SessionInfo(session);
        var vars = session.Variables.ToList();

        return format.ToLowerInvariant() switch
        {
            "summary" => new GetResult
            {
                Ok = true, TraceId = traceId, IsRunning = session.IsRunning,
                SessionInfo = info, Variables = vars,
                Summary = ComputeSummary(vars, events),
            },
            "csv" => new GetResult
            {
                Ok = true, TraceId = traceId, IsRunning = session.IsRunning,
                SessionInfo = info, Variables = vars,
                Csv = FormatCsv(vars, events),
            },
            _ => new GetResult
            {
                Ok = true, TraceId = traceId, IsRunning = session.IsRunning,
                SessionInfo = info, Variables = vars,
                Events = events.ToList(),
            },
        };
    }

    public GetResult StopTrace(string traceId)
    {
        if (!_sessions.TryGetValue(traceId, out var session))
            return new GetResult { Ok = false, Error = $"Trace not found: '{traceId}'" };

        session.IsRunning = false;
        session.AutoStopTimer?.Dispose();
        Unsubscribe(session);
        _sessions.TryRemove(traceId, out _);

        var events = session.Events.ToArray();
        return new GetResult
        {
            Ok = true,
            TraceId = traceId,
            IsRunning = false,
            SessionInfo = SessionInfo(session),
            Variables = session.Variables.ToList(),
            Events = events.ToList(),
            Summary = ComputeSummary(session.Variables, events),
        };
    }

    private void AutoStop(string traceId)
    {
        if (!_sessions.TryGetValue(traceId, out var session)) return;
        if (!session.IsRunning) return;
        session.IsRunning = false;
        session.LastAccessUtc = DateTime.UtcNow;
        Unsubscribe(session);
        _log.LogInformation("Trace {TraceId} auto-stopped: {Events} events", traceId, session.Events.Count);
    }

    private void Reap()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, session) in _sessions)
        {
            if (session.IsRunning) continue;
            if (now - session.LastAccessUtc < StaleSessionTtl) continue;
            if (_sessions.TryRemove(id, out var removed))
            {
                removed.AutoStopTimer?.Dispose();
                _log.LogInformation("Reaped stale trace {TraceId}", id);
            }
        }
    }

    private void Unsubscribe(TraceSession session)
    {
        foreach (var (path, (sym, handler)) in session.Subscriptions)
        {
            try
            {
                sym.ValueChanged -= handler;
                sym.NotificationSettings = NotificationSettings.Default;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Unsubscribe {Path} failed", path); }
        }
        session.Subscriptions.Clear();
    }

    private void OnValueChanged(TraceSession session, string path, ValueChangedEventArgs e)
    {
        if (!session.IsRunning) return;
        var ts = (long)(DateTime.UtcNow - session.StartTime).TotalMilliseconds;
        session.Events.Enqueue(new TraceEvent
        {
            TimestampMs = ts,
            Changes = new Dictionary<string, object?> { { path, e.Value } },
        });
        session.LastValues[path] = e.Value;
        while (session.Events.Count > session.MaxEvents) session.Events.TryDequeue(out _);
    }

    private List<ISymbol> Resolve(
        ISymbolCollection<ISymbol> symbols,
        string? paths, string? pattern, string? parentPath,
        int maxSubs, CancellationToken ct)
    {
        var result = new List<ISymbol>();

        if (!string.IsNullOrEmpty(pattern))
        {
            Regex regex;
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexPerMatchTimeout); }
            catch (ArgumentException ex) { throw new InvalidOperationException($"Invalid regex: {ex.Message}"); }

            IEnumerable<ISymbol> source;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = symbols.FirstOrDefault(s =>
                    s.InstancePath.Equals(parentPath, StringComparison.OrdinalIgnoreCase));
                if (parent is null) throw new InvalidOperationException($"Parent symbol not found: '{parentPath}'");
                source = parent.SubSymbols.Cast<ISymbol>();
            }
            else
            {
                source = symbols.Cast<ISymbol>();
            }

            Func<ISymbol, bool> selector = s =>
            {
                ct.ThrowIfCancellationRequested();
                try { return regex.IsMatch(s.InstancePath); }
                catch (RegexMatchTimeoutException) { return false; }
            };
            var iterator = new SymbolIterator(source, recurse: true, selector: selector);
            foreach (var sym in iterator)
            {
                if (result.Count >= maxSubs) break;
                result.Add(sym);
            }
        }
        else if (!string.IsNullOrEmpty(paths))
        {
            foreach (var raw in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (result.Count >= maxSubs) break;
                var sym = symbols.FirstOrDefault(s => s.InstancePath.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (sym is null) throw new InvalidOperationException($"Symbol not found: '{raw}'");
                result.Add(sym);
            }
        }
        else
        {
            throw new ArgumentException("Either 'paths' or 'pattern' must be provided.");
        }

        return result;
    }

    private static TraceSessionInfo SessionInfo(TraceSession s) => new()
    {
        TraceId = s.TraceId,
        IsRunning = s.IsRunning,
        StartTime = s.StartTime.ToString("o"),
        EventCount = s.Events.Count,
        VariableCount = s.Variables.Count,
        ElapsedMs = (int)(DateTime.UtcNow - s.StartTime).TotalMilliseconds,
    };

    private static string ClassifyVariable(ISymbol sym)
    {
        var cat = sym.DataType?.Category ?? DataTypeCategory.Primitive;
        var name = sym.DataType?.Name?.ToUpperInvariant() ?? "";
        if (cat == DataTypeCategory.String || name.StartsWith("STRING") || name.StartsWith("WSTRING"))
            return "state";
        if (cat is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock) return "struct";
        if (cat == DataTypeCategory.Enum) return "discrete";
        if (name is "REAL" or "LREAL") return "analog";
        return "discrete";
    }

    private static List<TraceVariableSummary> ComputeSummary(List<TracedVariable> vars, TraceEvent[] events)
    {
        var summaries = new List<TraceVariableSummary>();
        foreach (var v in vars)
        {
            var values = events
                .Where(e => e.Changes.ContainsKey(v.Path))
                .Select(e => e.Changes[v.Path])
                .ToList();
            var summary = new TraceVariableSummary
            {
                Path = v.Path,
                ChangeCount = Math.Max(0, values.Count - 1),
                FirstValue = values.FirstOrDefault(),
                LastValue = values.LastOrDefault(),
            };
            var nums = new List<double>();
            foreach (var val in values)
            {
                if (val is IConvertible c)
                {
                    try { nums.Add(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
                }
            }
            if (nums.Count > 0)
            {
                summary.MinValue = nums.Min();
                summary.MaxValue = nums.Max();
                summary.Average = Math.Round(nums.Average(), 4);
            }
            summaries.Add(summary);
        }
        return summaries;
    }

    private static string FormatCsv(List<TracedVariable> vars, TraceEvent[] events)
    {
        var sb = new System.Text.StringBuilder();
        var paths = vars.Select(v => v.Path).ToList();
        sb.Append("timestamp_ms");
        foreach (var p in paths) sb.Append(',').Append(EscapeCsv(p));
        sb.AppendLine();

        var lastKnown = paths.ToDictionary(p => p, _ => "");
        foreach (var ev in events)
        {
            foreach (var (p, val) in ev.Changes)
                if (lastKnown.ContainsKey(p)) lastKnown[p] = FormatCsvValue(val);
            sb.Append(ev.TimestampMs);
            foreach (var p in paths) sb.Append(',').Append(lastKnown[p]);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatCsvValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        IConvertible c => c.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => EscapeCsv(value.ToString() ?? ""),
    };

    private static string EscapeCsv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reaper?.Dispose();
        foreach (var id in _sessions.Keys.ToList())
        {
            try { StopTrace(id); } catch { }
        }
    }
}

internal sealed class TraceSession
{
    public string TraceId { get; }
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public ConcurrentQueue<TraceEvent> Events { get; } = new();
    public List<TracedVariable> Variables { get; } = new();
    public Dictionary<string, (IValueSymbol Symbol, EventHandler<ValueChangedEventArgs> Handler)> Subscriptions { get; } = new();
    public ConcurrentDictionary<string, object?> LastValues { get; } = new();
    public int MaxEvents { get; }
    public int MaxDurationMs { get; }
    public Timer? AutoStopTimer { get; set; }
    public volatile bool IsRunning;
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;

    public TraceSession(string id, int maxEvents, int maxDurationMs)
    {
        TraceId = id;
        MaxEvents = maxEvents;
        MaxDurationMs = maxDurationMs;
    }
}

public sealed class TraceEvent
{
    public long TimestampMs { get; init; }
    public Dictionary<string, object?> Changes { get; init; } = new();
}

public sealed class TracedVariable
{
    public string Path { get; init; } = "";
    public string? DataType { get; init; }
    public string? Category { get; init; }
    public int Size { get; init; }
}

public sealed class TraceSessionInfo
{
    public string? TraceId { get; init; }
    public bool IsRunning { get; init; }
    public string? StartTime { get; init; }
    public int EventCount { get; init; }
    public int VariableCount { get; init; }
    public int ElapsedMs { get; init; }
}

public sealed class TraceVariableSummary
{
    public string Path { get; init; } = "";
    public int ChangeCount { get; init; }
    public object? FirstValue { get; set; }
    public object? LastValue { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? Average { get; set; }
}
