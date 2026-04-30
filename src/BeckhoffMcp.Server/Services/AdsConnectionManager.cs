using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Manages a single AdsClient connection to the configured target. Uses the
/// AdsOverMqtt plugin (loaded automatically via MEF when the assembly is
/// present in the binaries folder) so no local TwinCAT install is required.
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

    public AdsConnectionManager(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _baseConfig = config;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<AdsConnectionManager>();

        // The AdsOverMqtt MqttRouter requires a local AmsNetId. If the user
        // didn't supply one, generate a stable random one for this process.
        if (string.IsNullOrEmpty(config.GetValue<string>("AmsRouter:NetId")))
        {
            var rnd = new Random();
            var generated = $"10.{rnd.Next(0, 256)}.{rnd.Next(0, 256)}.{rnd.Next(0, 256)}.1.1";
            _overrides["AmsRouter:NetId"] = generated;
            _log.LogInformation("AmsRouter:NetId not configured — generated random {NetId}", generated);
            _config = new ConfigurationBuilder()
                .AddConfiguration(_baseConfig)
                .AddInMemoryCollection(_overrides)
                .Build();
        }
        else
        {
            _config = config;
        }
        GlobalConfiguration.Configuration = _config;
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
    /// Atomic full reconfigure: applies optional Mqtt overrides, sets intent,
    /// drops any stale session, opens a new one, and returns its connection.
    /// All under a single lock so concurrent tool calls observe a consistent
    /// state.
    /// </summary>
    public IAdsConnection Configure(string netId, int port,
        string? mqttBroker, int? mqttPort, string? mqttTopic, out bool overrideApplied)
    {
        lock (_lock)
        {
            overrideApplied = mqttBroker != null || mqttPort != null || mqttTopic != null;
            if (mqttBroker != null) _overrides["AmsRouter:Mqtt:0:Address"] = mqttBroker;
            if (mqttPort != null)   _overrides["AmsRouter:Mqtt:0:Port"]    = mqttPort.ToString();
            if (mqttTopic != null)  _overrides["AmsRouter:Mqtt:0:Topic"]   = mqttTopic;
            if (overrideApplied)
            {
                _config = new ConfigurationBuilder()
                    .AddConfiguration(_baseConfig)
                    .AddInMemoryCollection(_overrides)
                    .Build();
                GlobalConfiguration.Configuration = _config;
                DisposeSessionLocked();
            }

            _intentTargetNetId = netId;
            _intentTargetPort = port;

            if (!AmsNetId.TryParse(netId, out var amsNetId))
                throw new ArgumentException($"Invalid AmsNetId: {netId}");

            DisposeSessionLocked();
            var addr = new AmsAddress(amsNetId, port);
            _log.LogInformation("Configure → opening AdsSession to {Address}", addr);
            _session = new AdsSession(addr, SessionSettings.Default, _config, _loggerFactory, null);
            _connection = (IAdsConnection)_session.Connect();
            _currentTargetNetId = netId;
            _currentTargetPort = port;
            return _connection;
        }
    }

    /// <summary>
    /// Apply runtime overrides to AmsRouter:Mqtt[0] (broker host/port/topic).
    /// Rebuilds the merged configuration and updates GlobalConfiguration so
    /// subsequent AdsSession instances pick the new MQTT broker up.
    /// Forces existing sessions to be reopened.
    /// </summary>
    public void ApplyMqttOverride(string? broker, int? port, string? topic)
    {
        lock (_lock)
        {
            if (broker is not null) _overrides["AmsRouter:Mqtt:0:Address"] = broker;
            if (port is not null)   _overrides["AmsRouter:Mqtt:0:Port"]    = port.ToString();
            if (topic is not null)  _overrides["AmsRouter:Mqtt:0:Topic"]   = topic;

            _config = new ConfigurationBuilder()
                .AddConfiguration(_baseConfig)
                .AddInMemoryCollection(_overrides)
                .Build();
            GlobalConfiguration.Configuration = _config;

            // Existing sessions still hold the old config — drop them so the
            // next request opens a fresh session with new settings.
            DisposeSessionLocked();
        }
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
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
