using System.ComponentModel;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class TraceTools
{
    private readonly TraceService _trace;
    public TraceTools(TraceService trace) => _trace = trace;

    [McpServerTool(Name = "beckhoff_trace_start"),
     Description("Subscribe to one or more PLC variables and record value changes via ADS notifications. " +
                 "Returns a trace_id for follow-up calls. Auto-stops after max_duration_ms. Use beckhoff_trace_get " +
                 "to read events and beckhoff_trace_stop to end early.")]
    public Task<object> TraceStart(
        [Description("Comma-separated symbol paths (e.g. 'MAIN.bStart, MAIN.nCounter'). Mutually exclusive with 'pattern'.")] string? paths = null,
        [Description("Regex to match symbol paths (e.g. 'MAIN\\.fb.*\\._state'). Mutually exclusive with 'paths'. Use parent_path to scope.")] string? pattern = null,
        [Description("Limit regex search to this parent symbol's children (e.g. 'MAIN').")] string? parent_path = null,
        [Description("Notification mode: 'onChange' (default) or 'cyclic'.")] string mode = "onChange",
        [Description("ADS notification cycle in ms (default 100).")] int cycle_time_ms = 100,
        [Description("Auto-stop after this many ms (default 10000 = 10s, max 60000 enforced).")] int max_duration_ms = 10000,
        [Description("Max events buffered (ring buffer, default 10000).")] int max_events = 10000,
        [Description("Max number of variable subscriptions (default 50).")] int max_subscriptions = 50,
        CancellationToken ct = default)
    {
        max_duration_ms = Math.Clamp(max_duration_ms, 100, 60000);
        var r = _trace.StartTrace(paths, pattern, parent_path,
            mode, cycle_time_ms, max_duration_ms, max_events, max_subscriptions, ct);
        if (!r.Ok)
            return Task.FromResult<object>(new { ok = false, error = r.Error });
        return Task.FromResult<object>(new
        {
            ok = true,
            trace_id = r.TraceId,
            max_duration_ms = r.MaxDurationMs,
            variable_count = r.Variables.Count,
            variables = r.Variables.Select(v => new { path = v.Path, type = v.DataType, category = v.Category, size = v.Size }),
        });
    }

    [McpServerTool(Name = "beckhoff_trace_get"),
     Description("Read buffered trace data. Format: 'events' (default — sparse change stream), 'summary' (per-variable stats), 'csv' (time-series). Use 'since' for incremental polling.")]
    public Task<object> TraceGet(
        [Description("Trace ID returned by beckhoff_trace_start.")] string trace_id,
        [Description("Output format: 'events' (default), 'summary', 'csv'.")] string format = "events",
        [Description("Only return events strictly after this timestamp_ms (incremental read). Optional.")] int? since = null,
        CancellationToken ct = default)
    {
        var r = _trace.GetTraceData(trace_id, format, since);
        if (!r.Ok) return Task.FromResult<object>(new { ok = false, error = r.Error });
        return Task.FromResult<object>(new
        {
            ok = true,
            trace_id = r.TraceId,
            is_running = r.IsRunning,
            session_info = r.SessionInfo,
            variables = r.Variables,
            events = r.Events,
            summary = r.Summary,
            csv = r.Csv,
        });
    }

    [McpServerTool(Name = "beckhoff_trace_stop"),
     Description("Stop a trace session, free its ADS subscriptions, and return the final event list and summary.")]
    public Task<object> TraceStop(
        [Description("Trace ID to stop.")] string trace_id,
        CancellationToken ct = default)
    {
        var r = _trace.StopTrace(trace_id);
        if (!r.Ok) return Task.FromResult<object>(new { ok = false, error = r.Error });
        return Task.FromResult<object>(new
        {
            ok = true,
            trace_id = r.TraceId,
            session_info = r.SessionInfo,
            variable_count = r.Variables?.Count ?? 0,
            event_count = r.Events?.Count ?? 0,
            events = r.Events,
            summary = r.Summary,
        });
    }
}
