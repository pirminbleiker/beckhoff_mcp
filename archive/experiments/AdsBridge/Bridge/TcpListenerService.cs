using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.AdsBridge.Bridge;

/// <summary>
/// Listens on TCP loopback (default 127.0.0.1:48898) — same protocol that pyads
/// (Linux) and ads-async use to talk to a local AMS Router.
/// Each frame is parsed, looked up against the route table, and forwarded.
/// </summary>
public sealed class TcpListenerService : BackgroundService
{
    private readonly ILogger<TcpListenerService> _log;
    private readonly RouteTable _routes;
    private readonly MqttForwarder _mqtt;
    private readonly TcpForwarder _tcp;
    private readonly LocalHandler _local;
    private readonly IPEndPoint _endpoint;

    public TcpListenerService(RouteTable routes, MqttForwarder mqtt, TcpForwarder tcp,
        LocalHandler local, ILogger<TcpListenerService> log, IPEndPoint? bind = null)
    {
        _log = log;
        _routes = routes;
        _mqtt = mqtt;
        _tcp = tcp;
        _local = local;
        _endpoint = bind ?? new IPEndPoint(IPAddress.Loopback, 48898);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var listener = new TcpListener(_endpoint);
        listener.Start();
        _log.LogInformation("AdsBridge TCP listener bound to {Endpoint}", _endpoint);

        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => HandleClientAsync(client, stop), stop);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stop)
    {
        var remote = client.Client.RemoteEndPoint;
        _log.LogInformation("Client connected: {Remote}", remote);
        try
        {
            using var _ = client;
            using var stream = client.GetStream();
            while (!stop.IsCancellationRequested)
            {
                var frame = await AmsFrame.ReadAsync(stream, stop);
                if (frame is null) break;
                _log.LogDebug("RX from {Remote}: {Frame}", remote, frame);
                await DispatchAsync(stream, frame, stop);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Client {Remote} error", remote);
        }
        _log.LogInformation("Client disconnected: {Remote}", remote);
    }

    private async Task DispatchAsync(Stream upstream, AmsFrame request, CancellationToken stop)
    {
        if (_local.IsForUs(request))
        {
            var resp = _local.Handle(request);
            await resp.WriteAsync(upstream, stop);
            _log.LogDebug("TX local response: {Frame}", resp);
            return;
        }

        var route = _routes.Resolve(request.TargetNetId);
        if (route is null)
        {
            _log.LogWarning("No route for target {Target} — replying TargetMachineNotFound", request.TargetNetId);
            await ReplyErrorAsync(upstream, request, errorCode: 0x07);
            return;
        }

        if (route.Type == TransportType.Mqtt)
        {
            try
            {
                var resp = await _mqtt.ForwardRequestAsync(request, TimeSpan.FromSeconds(5), stop);
                await resp.WriteAsync(upstream, stop);
                _log.LogDebug("TX to client: {Frame}", resp);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("MQTT forward timeout for invokeId={InvokeId}", request.InvokeId);
                await ReplyErrorAsync(upstream, request, errorCode: 0x745);
            }
        }
        else
        {
            try
            {
                var resp = await _tcp.ForwardRequestAsync(route, request, TimeSpan.FromSeconds(5), stop);
                await resp.WriteAsync(upstream, stop);
                _log.LogDebug("TX to client (TCP forwarded): {Frame}", resp);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TCP forward failed for {Target}", request.TargetNetId);
                await ReplyErrorAsync(upstream, request, errorCode: 0x745);
            }
        }
    }

    private static async Task ReplyErrorAsync(Stream upstream, AmsFrame request, uint errorCode)
    {
        var resp = new AmsFrame
        {
            TargetNetId = request.SourceNetId,
            TargetPort = request.SourcePort,
            SourceNetId = request.TargetNetId,
            SourcePort = request.TargetPort,
            CommandId = request.CommandId,
            StateFlags = (ushort)(request.StateFlags | 0x01),
            ErrorCode = errorCode,
            InvokeId = request.InvokeId,
            Payload = Array.Empty<byte>(),
        };
        await resp.WriteAsync(upstream, CancellationToken.None);
    }
}
