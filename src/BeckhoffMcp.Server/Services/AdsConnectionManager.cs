using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;
using TwinCAT.Ads.TcpRouter;

namespace BeckhoffMcp.Server.Services;

public enum AdsTransport
{
    /// <summary>AdsOverMqtt — TF6720 plugin, no local router process.</summary>
    Mqtt,
    /// <summary>Plain ADS over TCP/IP (port 48898). Requires an in-process AmsTcpIpRouter and a backroute on the PLC.</summary>
    Tcp,
}

/// <summary>
/// Manages a single AdsClient connection to the configured target. Supports two
/// transports:
/// <list type="bullet">
/// <item>MQTT (default): AdsSession uses the AdsOverMqtt MEF plugin directly —
/// no router process needed.</item>
/// <item>TCP: an in-process <see cref="AmsTcpIpRouter"/> is started on demand;
/// AdsSession connects via loopback and the router forwards AMS frames to the
/// target over TCP/IP. Requires a backroute on the PLC pointing to our
/// AmsNetId, otherwise the PLC drops the AMS reply.</item>
/// </list>
/// </summary>
public sealed class AdsConnectionManager : IAsyncDisposable
{
    private readonly IConfiguration _baseConfig;
    private IConfiguration _config;
    private readonly Dictionary<string, string?> _overrides = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsConnectionManager> _log;
    private readonly object _lock = new();
    private AdsSession? _session;
    private IAdsConnection? _connection;
    /// <summary>Target the user explicitly chose (via connect or initial config). Persists across session resets.</summary>
    private string? _intentTargetNetId;
    private int _intentTargetPort;
    /// <summary>Target of the actual live session. May lag behind intent if reconnect failed.</summary>
    private string? _currentTargetNetId;
    private int _currentTargetPort;
    private AdsTransport _activeTransport = AdsTransport.Mqtt;
    private AmsTcpIpRouter? _tcpRouter;
    private CancellationTokenSource? _tcpRouterCts;
    private Task? _tcpRouterTask;

    public AdsConnectionManager(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _baseConfig = config;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<AdsConnectionManager>();

        // A stable local AmsNetId is required: the PLC's <Route> entry must
        // match it, and the PLC silently drops AMS frames if it doesn't. If
        // the user didn't configure one, we generate it once AND persist it to
        // appsettings.json so the same NetId survives restarts and the user
        // only has to register a backroute on the PLC once.
        var configured = config.GetValue<string>("AmsRouter:NetId");
        if (string.IsNullOrEmpty(configured))
        {
            var rnd = new Random();
            configured = $"10.{rnd.Next(0, 256)}.{rnd.Next(0, 256)}.{rnd.Next(0, 256)}.1.1";
            _log.LogWarning("AmsRouter:NetId not configured — generated {NetId} and persisting to appsettings.json", configured);
            _overrides["AmsRouter:NetId"] = configured;
            _config = new ConfigurationBuilder()
                .AddConfiguration(_baseConfig)
                .AddInMemoryCollection(_overrides)
                .Build();
            TryPersistGeneratedNetId(configured);
        }
        else
        {
            _config = config;
            _log.LogInformation("Local AmsNetId (configured): {NetId}", configured);
        }
        GlobalConfiguration.Configuration = _config;
    }

    public string LocalNetId
        => _config.GetValue<string>("AmsRouter:NetId") ?? "(unset)";

    public string LocalName
        => _config.GetValue<string>("AmsRouter:Name") ?? "BeckhoffMcp";

    /// <summary>
    /// Writes the freshly generated NetId back into the appsettings.json that
    /// sits next to the running executable so the next startup picks the same
    /// one. Best-effort: if the file is read-only or absent we just log and
    /// keep the in-memory override.
    /// </summary>
    private void TryPersistGeneratedNetId(string netId)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            // Bootstrap a minimal file if none exists yet — the disk override
            // is optional but persisting the generated NetId there means the
            // next launch picks the same identity (so a backroute on the PLC
            // keeps matching).
            string json = File.Exists(path)
                ? File.ReadAllText(path)
                : "{}";
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            // Rebuild the JSON tree with AmsRouter:NetId set/added.
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms,
                       new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                bool sawAmsRouter = false;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("AmsRouter") &&
                        prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        sawAmsRouter = true;
                        writer.WritePropertyName("AmsRouter");
                        writer.WriteStartObject();
                        bool wroteNetId = false;
                        foreach (var sub in prop.Value.EnumerateObject())
                        {
                            if (sub.NameEquals("NetId"))
                            {
                                writer.WriteString("NetId", netId);
                                wroteNetId = true;
                            }
                            else
                            {
                                sub.WriteTo(writer);
                            }
                        }
                        if (!wroteNetId) writer.WriteString("NetId", netId);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!sawAmsRouter)
                {
                    writer.WritePropertyName("AmsRouter");
                    writer.WriteStartObject();
                    writer.WriteString("NetId", netId);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
            _log.LogInformation("Persisted generated NetId to {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist generated NetId — using in-memory override only");
        }
    }

    /// <summary>
    /// Atomically remember the user's intended target. Called early by connect
    /// so concurrent getters see the new target immediately, even before the
    /// session has actually been (re)opened.
    /// </summary>
    public void SetIntent(string netId, int port)
    {
        lock (_lock)
        {
            _intentTargetNetId = netId;
            _intentTargetPort = port;
        }
    }

    /// <summary>
    /// Atomic full reconfigure for the MQTT transport: applies optional Mqtt
    /// overrides, sets intent, drops any stale session, opens a new one. All
    /// under a single lock so concurrent tool calls observe a consistent state.
    /// </summary>
    public IAdsConnection Configure(string netId, int port,
        string? mqttBroker, int? mqttPort, string? mqttTopic, out bool overrideApplied)
    {
        lock (_lock)
        {
            overrideApplied = mqttBroker != null || mqttPort != null || mqttTopic != null
                              || _activeTransport != AdsTransport.Mqtt;

            // Switching back to MQTT — tear down the TCP router and clear any
            // RemoteConnections + ChannelProtocol overrides we left behind.
            if (_activeTransport != AdsTransport.Mqtt)
            {
                StopTcpRouterLocked();
                ClearTcpOverridesLocked();
            }
            _overrides["AmsRouter:ChannelProtocol"] = "AdsOverMqtt";

            if (mqttBroker != null) _overrides["AmsRouter:Mqtt:0:Address"] = mqttBroker;
            if (mqttPort != null)   _overrides["AmsRouter:Mqtt:0:Port"]    = mqttPort.ToString();
            if (mqttTopic != null)  _overrides["AmsRouter:Mqtt:0:Topic"]   = mqttTopic;

            RebuildConfigLocked();
            DisposeSessionLocked();

            _intentTargetNetId = netId;
            _intentTargetPort = port;

            if (!AmsNetId.TryParse(netId, out var amsNetId))
                throw new ArgumentException($"Invalid AmsNetId: {netId}");

            var addr = new AmsAddress(amsNetId, port);
            _log.LogInformation("Configure (MQTT) → opening AdsSession to {Address}", addr);
            _session = new AdsSession(addr, SessionSettings.Default, _config, _loggerFactory, null);
            _connection = (IAdsConnection)_session.Connect();
            _currentTargetNetId = netId;
            _currentTargetPort = port;
            _activeTransport = AdsTransport.Mqtt;
            return _connection;
        }
    }

    /// <summary>
    /// Atomic full reconfigure for the TCP transport: starts (or reuses) an
    /// in-process AmsTcpIpRouter, registers a TCP_IP route to <paramref name="targetIp"/>,
    /// then opens a fresh AdsSession that talks to the router via loopback.
    /// </summary>
    public IAdsConnection ConfigureTcp(string netId, int port, string targetIp, string? routeName)
    {
        lock (_lock)
        {
            if (!AmsNetId.TryParse(netId, out var amsNetId))
                throw new ArgumentException($"Invalid AmsNetId: {netId}");

            _intentTargetNetId = netId;
            _intentTargetPort = port;

            // Switching transports — tear down the existing AdsSession (and any
            // cached port sessions) before re-pointing GlobalConfiguration.
            DisposeSessionLocked();

            // Configure the router to talk plain TCP. Loopback channel so the
            // AdsClient connects to the router via 127.0.0.1.
            _overrides["AmsRouter:ChannelProtocol"] = "Ads";
            _overrides["AmsRouter:ChannelPortType"] = "Loopback";
            _overrides["AmsRouter:RemoteConnections:0:Name"]    = routeName ?? $"mcp-{netId}";
            _overrides["AmsRouter:RemoteConnections:0:Address"] = targetIp;
            _overrides["AmsRouter:RemoteConnections:0:NetId"]   = netId;
            _overrides["AmsRouter:RemoteConnections:0:Type"]    = "TCP_IP";

            RebuildConfigLocked();

            // Router lifecycle: start once, replace the route on subsequent
            // ConfigureTcp calls. We can't safely re-bind 48898, so we keep the
            // router alive across reconfigurations.
            EnsureTcpRouterStartedLocked();
            ReplaceTcpRouteLocked(amsNetId, targetIp, routeName ?? $"mcp-{netId}");

            var addr = new AmsAddress(amsNetId, port);
            _log.LogInformation("Configure (TCP) → opening AdsSession to {Address} via in-process router", addr);
            _session = new AdsSession(addr, SessionSettings.Default, _config, _loggerFactory, null);
            _connection = (IAdsConnection)_session.Connect();
            _currentTargetNetId = netId;
            _currentTargetPort = port;
            _activeTransport = AdsTransport.Tcp;
            return _connection;
        }
    }

    private void RebuildConfigLocked()
    {
        _config = new ConfigurationBuilder()
            .AddConfiguration(_baseConfig)
            .AddInMemoryCollection(_overrides)
            .Build();
        GlobalConfiguration.Configuration = _config;
    }

    private void ClearTcpOverridesLocked()
    {
        _overrides.Remove("AmsRouter:ChannelPortType");
        _overrides.Remove("AmsRouter:RemoteConnections:0:Name");
        _overrides.Remove("AmsRouter:RemoteConnections:0:Address");
        _overrides.Remove("AmsRouter:RemoteConnections:0:NetId");
        _overrides.Remove("AmsRouter:RemoteConnections:0:Type");
    }

    private void EnsureTcpRouterStartedLocked()
    {
        if (_tcpRouter is { IsRunning: true }) return;

        // Drop any previous instance — if it's not running, recreate it.
        StopTcpRouterLocked();

        _tcpRouterCts = new CancellationTokenSource();
        _tcpRouter = new AmsTcpIpRouter(_config, _loggerFactory);
        _log.LogInformation("Starting in-process AmsTcpIpRouter (NetId={NetId})", _tcpRouter.NetId);
        _tcpRouterTask = _tcpRouter.StartAsync(_tcpRouterCts.Token);
    }

    private void ReplaceTcpRouteLocked(AmsNetId targetNetId, string targetIp, string name)
    {
        if (_tcpRouter is null) return;
        try { _tcpRouter.RemoveRoute(targetNetId); } catch { }
        var route = new Route(name, targetNetId, targetIp);
        _tcpRouter.AddRoute(route);
        _log.LogInformation("TCP route registered: {Name} {NetId} → {Ip}", name, targetNetId, targetIp);
    }

    private void StopTcpRouterLocked()
    {
        try { _tcpRouter?.Stop(); } catch { }
        try { _tcpRouterCts?.Cancel(); } catch { }
        _tcpRouter = null;
        _tcpRouterCts = null;
        _tcpRouterTask = null;
    }

    public AdsTransport ActiveTransport
    {
        get { lock (_lock) return _activeTransport; }
    }

    public bool TcpRouterRunning
    {
        get { lock (_lock) return _tcpRouter is { IsRunning: true }; }
    }

    public string ActiveMqttBroker
        => _config.GetValue<string>("AmsRouter:Mqtt:0:Address") ?? "(unset)";
    public int ActiveMqttPort
        => _config.GetValue<int?>("AmsRouter:Mqtt:0:Port") ?? 0;
    public string ActiveMqttTopic
        => _config.GetValue<string>("AmsRouter:Mqtt:0:Topic") ?? "(unset)";

    public string TargetNetId
    {
        get
        {
            lock (_lock)
            {
                return _intentTargetNetId
                    ?? _currentTargetNetId
                    ?? _config.GetValue<string>("Beckhoff:TargetNetId")
                    ?? throw new InvalidOperationException("No target. Call beckhoff_connect first.");
            }
        }
    }

    public int TargetPort
    {
        get
        {
            lock (_lock)
            {
                if (_intentTargetPort > 0) return _intentTargetPort;
                if (_currentTargetPort > 0) return _currentTargetPort;
                return _config.GetValue<int?>("Beckhoff:TargetPort") ?? 851;
            }
        }
    }

    public bool IsConnected => _connection?.IsConnected ?? false;

    public IAdsConnection EnsureConnected() => EnsureConnectedTo(TargetNetId, TargetPort);

    private readonly Dictionary<int, AdsSession> _portSessions = new();

    /// <summary>
    /// Opens a fresh connection to the same NetId on a different port. Caller
    /// is responsible for disposing via <see cref="DisposePortConnection"/>.
    /// We do NOT cache port sessions because the underlying AdsClient state
    /// becomes invalid after a failed read on a non-existent port.
    /// </summary>
    public (IAdsConnection conn, AdsSession session) OpenPortSession(int port)
    {
        if (!AmsNetId.TryParse(TargetNetId, out var amsNetId))
            throw new ArgumentException($"Invalid AmsNetId: {TargetNetId}");

        var addr = new AmsAddress(amsNetId, port);
        _log.LogDebug("Opening AdsSession to {Address}", addr);
        var session = new AdsSession(addr, SessionSettings.Default, _config, _loggerFactory, null);
        var conn = (IAdsConnection)session.Connect();
        return (conn, session);
    }

    /// <summary>
    /// Caches port sessions and reuses them. Disposing the AdsSession for the
    /// AdsOverMqtt plugin breaks the shared static MqttRouter, so we never
    /// dispose mid-flight; cached sessions are only released when the target
    /// changes or the manager itself is disposed.
    /// </summary>
    public IAdsConnection EnsureConnectedToPort(int port)
    {
        lock (_lock)
        {
            if (_portSessions.TryGetValue(port, out var existing))
                return (IAdsConnection)existing.Connect();

            var (conn, session) = OpenPortSession(port);
            _portSessions[port] = session;
            return conn;
        }
    }

    public IAdsConnection EnsureConnectedTo(string netId, int port)
    {
        lock (_lock)
        {
            // Persist the user's intent immediately so subsequent calls keep
            // the chosen target even if the actual session has to be rebuilt.
            _intentTargetNetId = netId;
            _intentTargetPort = port;

            if (_connection is { IsConnected: true } &&
                _currentTargetNetId == netId &&
                _currentTargetPort == port)
                return _connection;

            DisposeSessionLocked();

            if (!AmsNetId.TryParse(netId, out var amsNetId))
                throw new ArgumentException($"Invalid AmsNetId: {netId}");

            var addr = new AmsAddress(amsNetId, port);
            _log.LogInformation("Opening AdsSession to {Address}", addr);

            _session = new AdsSession(addr, SessionSettings.Default, _config, _loggerFactory, null);
            _connection = (IAdsConnection)_session.Connect();
            _currentTargetNetId = netId;
            _currentTargetPort = port;
            return _connection;
        }
    }

    public void Disconnect()
    {
        lock (_lock) DisposeSessionLocked();
    }

    private void DisposeSessionLocked()
    {
        try { _connection?.Disconnect(); } catch { }
        _session?.Dispose();
        _session = null;
        _connection = null;
        _currentTargetNetId = null;
        _currentTargetPort = 0;
        foreach (var s in _portSessions.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _portSessions.Clear();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            DisposeSessionLocked();
            StopTcpRouterLocked();
        }
        return ValueTask.CompletedTask;
    }
}
