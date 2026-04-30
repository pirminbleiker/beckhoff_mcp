using System.ComponentModel;
using System.Text.RegularExpressions;
using BeckhoffMcp.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class DiscoveryTools
{
    private readonly AdsConnectionManager _ads;
    private readonly NetworkDiscovery _network;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscoveryTools> _log;

    public DiscoveryTools(AdsConnectionManager ads, NetworkDiscovery network, IConfiguration config, ILoggerFactory lf)
    {
        _ads = ads;
        _network = network;
        _config = config;
        _log = lf.CreateLogger<DiscoveryTools>();
    }

    [McpServerTool(Name = "beckhoff_discover_network"),
     Description("Active network scan for Beckhoff devices: UDP 48899 ADS probe + TCP port scan across one or more subnets. " +
                 "Auto-detects local subnets if 'subnets' is empty. Use 'targets' for explicit IP probing. " +
                 "Returns hosts that answered ADS-discovery or have any common Beckhoff port open. Agent uses this to choose a target before beckhoff_connect.")]
    public async Task<object> DiscoverNetwork(
        [Description("CIDR subnets to scan (e.g. ['192.168.71.0/24']). If null, auto-detects from local interfaces.")] string[]? subnets = null,
        [Description("Explicit IP targets — bypasses subnet sweep when provided.")] string[]? targets = null,
        [Description("UDP 48899 timeout per host (ms, default 500).")] int udp_timeout_ms = 500,
        [Description("TCP connect timeout per port (ms, default 800).")] int tcp_timeout_ms = 800,
        [Description("Max concurrent hosts probed (default 64).")] int max_parallelism = 64,
        CancellationToken ct = default)
    {
        var ipList = new List<string>();

        if (targets is { Length: > 0 })
        {
            ipList.AddRange(targets);
        }
        else
        {
            var nets = subnets is { Length: > 0 } ? subnets : NetworkDiscovery.AutoDetectSubnets().ToArray();
            foreach (var cidr in nets)
            {
                try { ipList.AddRange(NetworkDiscovery.ExpandSubnet(cidr)); }
                catch (Exception ex) { _log.LogWarning(ex, "ExpandSubnet failed for {Cidr}", cidr); }
            }
        }

        if (ipList.Count == 0)
            return new { error = "No targets to scan and no auto-detectable subnets." };

        ipList = ipList.Distinct().ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await _network.ProbeRangeAsync(ipList, udp_timeout_ms, tcp_timeout_ms, max_parallelism, ct);

        return new
        {
            scanned_count = ipList.Count,
            elapsed_ms = sw.ElapsedMilliseconds,
            host_count = results.Count,
            hosts = results.Select(h => new
            {
                ip = h.IpAddress,
                ams_net_id = h.Ads?.AmsNetId,
                ams_port = h.Ads?.AmsPort,
                hostname = h.Ads?.HostName,
                os = h.Ads?.OsName,
                twincat_version = h.Ads?.TwinCatVersion,
                fingerprint = h.Ads?.Fingerprint,
                open_tcp = h.OpenPorts.Select(p => new { port = p.Port, label = p.Label }),
                elapsed_ms = h.ElapsedMs,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "beckhoff_discover"),
     Description("Discover online TwinCAT systems by listening to '<topic>/+/info' on the configured MQTT broker. " +
                 "Returns peers that announced themselves on the broker within the listen window.")]
    public async Task<object> Discover(
        [Description("MQTT broker host. If omitted, uses AmsRouter:Mqtt[0]:Address from config.")] string? broker_host = null,
        [Description("MQTT broker port. Defaults to AmsRouter:Mqtt[0]:Port (typically 1883).")] int? broker_port = null,
        [Description("Topic root to scan. Defaults to AmsRouter:Mqtt[0]:Topic (typically 'AdsOverMqtt').")] string? topic_root = null,
        [Description("How many seconds to listen (default 3, max 10).")] int listen_seconds = 3)
    {
        listen_seconds = Math.Clamp(listen_seconds, 1, 10);

        var host = broker_host ?? _config.GetValue<string>("AmsRouter:Mqtt:0:Address");
        var port = broker_port ?? _config.GetValue<int?>("AmsRouter:Mqtt:0:Port") ?? 1883;
        var topic = topic_root ?? _config.GetValue<string>("AmsRouter:Mqtt:0:Topic") ?? "AdsOverMqtt";

        if (string.IsNullOrEmpty(host))
            return new { error = "No broker host given and AmsRouter:Mqtt[0]:Address not configured." };

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var peers = new Dictionary<string, Dictionary<string, string?>>();

        client.ApplicationMessageReceivedAsync += args =>
        {
            try
            {
                var t = args.ApplicationMessage.Topic;
                var payload = System.Text.Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                var m = Regex.Match(t, @"^[^/]+/(?<netId>[\d.]+)/info$");
                if (!m.Success) return Task.CompletedTask;
                var netId = m.Groups["netId"].Value;
                var entry = peers.TryGetValue(netId, out var e) ? e : new Dictionary<string, string?>();
                entry["net_id"] = netId;
                var nameMatch = Regex.Match(payload, "name=['\"](?<n>[^'\"]+)['\"]");
                if (nameMatch.Success) entry["name"] = nameMatch.Groups["n"].Value;
                var osMatch = Regex.Match(payload, "osVersion=['\"](?<v>[^'\"]+)['\"]");
                if (osMatch.Success) entry["os_version"] = osMatch.Groups["v"].Value;
                var onlineMatch = Regex.Match(payload, ">(?<o>true|false)<");
                if (onlineMatch.Success) entry["online"] = onlineMatch.Groups["o"].Value;
                peers[netId] = entry;
            }
            catch (Exception ex) { _log.LogDebug(ex, "discover parse failed"); }
            return Task.CompletedTask;
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(listen_seconds + 5));
            var opts = new MqttClientOptionsBuilder()
                .WithClientId($"BeckhoffMcp-Discover-{Guid.NewGuid():N}")
                .WithTcpServer(host, port)
                .WithCleanSession(true)
                .Build();

            await client.ConnectAsync(opts, cts.Token);
            await client.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic($"{topic}/+/info")
                .WithRetainHandling(MqttRetainHandling.SendAtSubscribe)
                .Build(), cts.Token);

            await Task.Delay(TimeSpan.FromSeconds(listen_seconds), cts.Token);
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            return new
            {
                error = ex.Message,
                broker = $"{host}:{port}",
                topic_root = topic,
                hint = "Check broker reachability and that PLCs publish on '<topic>/<netid>/info'.",
            };
        }

        return new
        {
            broker = $"{host}:{port}",
            topic_root = topic,
            listen_seconds,
            peer_count = peers.Count,
            peers = peers.Values
                .OrderBy(p => p.GetValueOrDefault("net_id"))
                .ToList(),
        };
    }

    [McpServerTool(Name = "beckhoff_connect"),
     Description("Set the active target (NetId + ADS port) and probe both the requested port AND SystemService (10000) " +
                 "in one call. Two transports: 'mqtt' (default, AdsOverMqtt plugin — no router needed) and 'tcp' " +
                 "(in-process AmsTcpIpRouter, requires target_ip and a backroute on the PLC for our local NetId). " +
                 "Returns: PLC runtime status for target_port, SystemService status (always 10000), plus the active transport.")]
    public async Task<object> Connect(
        [Description("Target AmsNetId (e.g. '169.254.34.222.1.1')")] string target_net_id,
        [Description("Target ADS port — typically 851 (PLC Runtime 1), 852 (PLC2), 0xFFFF (EtherCAT). Default 851.")] int target_port = 851,
        [Description("Transport: 'mqtt' (default) or 'tcp'.")] string transport = "mqtt",
        [Description("Required for transport='tcp': IP/hostname of the PLC. Ignored for MQTT.")] string? target_ip = null,
        [Description("Optional route name for the registered TCP_IP route. Defaults to 'mcp-<netId>'.")] string? route_name = null,
        [Description("MQTT broker host override (transport='mqtt').")] string? mqtt_broker = null,
        [Description("MQTT broker port override (transport='mqtt').")] int? mqtt_port = null,
        [Description("MQTT topic root override (transport='mqtt'). Default 'AdsOverMqtt'.")] string? mqtt_topic = null,
        [Description("Probe timeout per port (ms). Default 10000. Bound the wait so a missing TCP backroute fails fast.")] int probe_timeout_ms = 10000,
        CancellationToken ct = default)
    {
        bool overrideApplied = false;
        var t = (transport ?? "mqtt").Trim().ToLowerInvariant();
        try
        {
            TwinCAT.Ads.IAdsConnection conn;
            if (t == "tcp")
            {
                if (string.IsNullOrWhiteSpace(target_ip))
                    return new
                    {
                        success = false,
                        error = "transport='tcp' requires target_ip (IP/hostname of the PLC).",
                        hint = "Use beckhoff_discover_network to find the IP for a given AmsNetId.",
                    };
                conn = _ads.ConfigureTcp(target_net_id, target_port, target_ip!, route_name);
                overrideApplied = true;
            }
            else
            {
                conn = _ads.Configure(target_net_id, target_port,
                    mqtt_broker, mqtt_port, mqtt_topic, out overrideApplied);
            }

            var runtime = await ProbePortAsync(conn, probe_timeout_ms, ct);

            // Always also probe the SystemService on port 10000 — it should
            // answer for any reachable Beckhoff target and tells us what kind
            // of TwinCAT install we're talking to (XAR/XAE, version, state).
            object systemService;
            try
            {
                var sysConn = _ads.EnsureConnectedToPort(10000);
                systemService = await ProbePortAsync(sysConn, probe_timeout_ms, ct);
            }
            catch (Exception ex)
            {
                systemService = new { ok = false, error = ex.Message };
            }

            return new
            {
                success = true,
                target_net_id,
                target_port,
                transport = _ads.ActiveTransport.ToString().ToLowerInvariant(),
                target_ip = t == "tcp" ? target_ip : null,
                tcp_router_running = _ads.TcpRouterRunning,
                local_net_id = _ads.LocalNetId,
                local_name = _ads.LocalName,
                backroute_hint = t == "tcp"
                    ? $"For TCP transport the PLC must have a static <Route> entry for our NetId '{_ads.LocalNetId}'. Without it the PLC silently drops AMS frames (visible as ClientSyncTimeOut)."
                    : null,
                mqtt_override_applied = overrideApplied,
                active_broker = _ads.ActiveMqttBroker,
                active_port = _ads.ActiveMqttPort,
                active_topic = _ads.ActiveMqttTopic,
                runtime,
                system_service = systemService,
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                target_net_id,
                target_port,
                transport = t,
                target_ip,
                local_net_id = _ads.LocalNetId,
                mqtt_override_applied = overrideApplied,
                active_broker = _ads.ActiveMqttBroker,
                active_topic = _ads.ActiveMqttTopic,
                error = ex.Message,
                hint = t == "tcp"
                    ? $"Verify target_ip:48898 is reachable AND the PLC has a static backroute for our local AmsNetId '{_ads.LocalNetId}'. Add a <Route> with NetId='{_ads.LocalNetId}' Type='TCP_IP' on the PLC, otherwise AMS frames are silently dropped."
                    : "Verify the AmsNetId is reachable via the configured MQTT broker. Use beckhoff_discover to list peers.",
            };
        }
    }

    /// <summary>Reads device info + ADS state for a connection, capped by <paramref name="timeoutMs"/>.</summary>
    private static async Task<object> ProbePortAsync(TwinCAT.Ads.IAdsConnection conn, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)));
        try
        {
            var info = await conn.ReadDeviceInfoAsync(timeoutCts.Token);
            object? device = info.Succeeded
                ? new
                {
                    name = info.DeviceInfo.Name,
                    version = $"{info.DeviceInfo.Version.Version}.{info.DeviceInfo.Version.Revision}.{info.DeviceInfo.Version.Build}",
                }
                : null;

            var state = await conn.ReadStateAsync(timeoutCts.Token);
            object? stateInfo = state.Succeeded
                ? new
                {
                    ads_state = state.State.AdsState.ToString(),
                    ads_state_code = (int)state.State.AdsState,
                    device_state = state.State.DeviceState,
                }
                : null;

            var ok = info.Succeeded || state.Succeeded;
            return new
            {
                ok,
                device,
                state = stateInfo,
                error = ok ? null : (info.Succeeded ? null : info.ErrorCode.ToString()),
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }
}
