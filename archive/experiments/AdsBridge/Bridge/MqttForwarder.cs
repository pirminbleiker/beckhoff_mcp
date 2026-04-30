using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BeckhoffMcp.AdsBridge.Bridge;

/// <summary>
/// Wraps an AMS frame as MQTT publish on topic
/// "&lt;baseTopic&gt;/&lt;targetNetId&gt;/ams" and subscribes to incoming
/// frames on "&lt;baseTopic&gt;/&lt;ourNetId&gt;/ams/#".
/// Routes responses back to the originating TCP client by InvokeId.
/// </summary>
public sealed class MqttForwarder : IAsyncDisposable
{
    private readonly ILogger<MqttForwarder> _log;
    private readonly MqttBrokerConfig _broker;
    private readonly AmsNetId _ourNetId;
    private readonly IMqttClient _client;
    private readonly ConcurrentDictionary<uint, Func<AmsFrame, Task>> _responseHandlers = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MqttForwarder(MqttBrokerConfig broker, AmsNetId ourNetId, ILogger<MqttForwarder> log)
    {
        _broker = broker;
        _ourNetId = ourNetId;
        _log = log;
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ConnectedAsync += OnConnected;
        _client.DisconnectedAsync += OnDisconnected;
        _client.ApplicationMessageReceivedAsync += OnMessage;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var opts = new MqttClientOptionsBuilder()
            .WithClientId($"AdsBridge-{Guid.NewGuid():N}")
            .WithTcpServer(_broker.Address, _broker.Port)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .Build();

        _log.LogInformation("MQTT connecting to {Host}:{Port} (topic={Topic})", _broker.Address, _broker.Port, _broker.Topic);
        await _client.ConnectAsync(opts, ct);

        var topic = $"{_broker.Topic}/{_ourNetId}/ams/#";
        _log.LogInformation("MQTT subscribe {Topic}", topic);
        await _client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build(), ct);

        var infoTopic = $"{_broker.Topic}/{_ourNetId}/info";
        var infoMsg = $"<info><online name='AdsBridge'>true</online></info>";
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(infoTopic)
            .WithPayload(infoMsg)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

        _connected.TrySetResult();
    }

    public async Task<AmsFrame> ForwardRequestAsync(AmsFrame request, TimeSpan timeout, CancellationToken ct)
    {
        await _connected.Task;
        var topic = $"{_broker.Topic}/{request.TargetNetId}/ams";
        var tcs = new TaskCompletionSource<AmsFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        _responseHandlers[request.InvokeId] = async resp =>
        {
            tcs.TrySetResult(resp);
            await Task.CompletedTask;
        };

        var bytes = request.ToAmsBytes();
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(bytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        _log.LogDebug("MQTT publish {Topic} ({Bytes} bytes, invokeId={InvokeId})", topic, bytes.Length, request.InvokeId);
        await _client.PublishAsync(msg, ct);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        finally
        {
            _responseHandlers.TryRemove(request.InvokeId, out _);
        }
    }

    private Task OnConnected(MqttClientConnectedEventArgs args)
    {
        _log.LogInformation("MQTT connected: {Result}", args.ConnectResult.ResultCode);
        return Task.CompletedTask;
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs args)
    {
        _log.LogWarning("MQTT disconnected: {Reason}", args.ReasonString ?? args.Reason.ToString());
        return Task.CompletedTask;
    }

    private async Task OnMessage(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var payload = args.ApplicationMessage.PayloadSegment.ToArray();
            if (payload.Length < AmsFrame.AmsHeaderSize)
            {
                _log.LogTrace("MQTT message on {Topic} too small: {Size}b", args.ApplicationMessage.Topic, payload.Length);
                return;
            }
            var frame = AmsFrame.FromAmsBytes(payload);
            _log.LogDebug("MQTT recv {Topic} → {Frame}", args.ApplicationMessage.Topic, frame);

            if (_responseHandlers.TryGetValue(frame.InvokeId, out var handler))
            {
                await handler(frame);
            }
            else
            {
                _log.LogTrace("No pending request for invokeId={InvokeId}", frame.InvokeId);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MQTT message handling failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        _client.Dispose();
    }
}
