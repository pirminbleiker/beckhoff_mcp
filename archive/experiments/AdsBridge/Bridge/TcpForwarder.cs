using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.AdsBridge.Bridge;

/// <summary>
/// Forwards AMS frames to a remote TwinCAT system via plain TCP/AMS (port 48898).
/// Maintains one persistent TCP connection per (host, port). Correlates responses
/// back to pending requests by InvokeId.
/// </summary>
public sealed class TcpForwarder : IAsyncDisposable
{
    private readonly ILogger<TcpForwarder> _log;
    private readonly ConcurrentDictionary<string, RemoteConnection> _connections = new();

    public TcpForwarder(ILogger<TcpForwarder> log) => _log = log;

    public async Task<AmsFrame> ForwardRequestAsync(RouteEntry route, AmsFrame request, TimeSpan timeout, CancellationToken ct)
    {
        var key = $"{route.Address}:{route.Port}";
        var conn = _connections.GetOrAdd(key, _ => new RemoteConnection(route.Address, route.Port, _log));
        return await conn.SendAndAwaitAsync(request, timeout, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections.Values) await c.DisposeAsync();
        _connections.Clear();
    }

    private sealed class RemoteConnection : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly ILogger _log;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<AmsFrame>> _pending = new();
        private readonly CancellationTokenSource _readerCts = new();
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Task? _readerTask;

        public RemoteConnection(string host, int port, ILogger log)
        {
            _host = host;
            _port = port;
            _log = log;
        }

        public async Task<AmsFrame> SendAndAwaitAsync(AmsFrame request, TimeSpan timeout, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct);
            var stream = _stream ?? throw new InvalidOperationException("Stream not initialised");

            var tcs = new TaskCompletionSource<AmsFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.InvokeId, tcs))
                throw new InvalidOperationException($"InvokeId {request.InvokeId} already pending");

            try
            {
                await _sendLock.WaitAsync(ct);
                try { await request.WriteAsync(stream, ct); }
                finally { _sendLock.Release(); }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                return await tcs.Task.WaitAsync(cts.Token);
            }
            finally
            {
                _pending.TryRemove(request.InvokeId, out _);
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client?.Connected == true) return;
            await DisposeConnectionAsync();

            _log.LogInformation("TCP forwarder connecting {Host}:{Port}", _host, _port);
            var client = new TcpClient();
            await client.ConnectAsync(_host, _port, ct);
            _client = client;
            _stream = client.GetStream();
            _readerTask = Task.Run(() => ReaderLoopAsync(_stream, _readerCts.Token));
        }

        private async Task ReaderLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var resp = await AmsFrame.ReadAsync(stream, ct);
                    if (resp is null) break;
                    if (_pending.TryRemove(resp.InvokeId, out var tcs))
                        tcs.TrySetResult(resp);
                    else
                        _log.LogTrace("TCP forwarder: no pending request for invokeId={Id}", resp.InvokeId);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "TCP forwarder reader loop ended");
                foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
            }
        }

        private async Task DisposeConnectionAsync()
        {
            try { _readerCts.Cancel(); } catch { }
            if (_readerTask is not null) try { await _readerTask; } catch { }
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeConnectionAsync();
            _readerCts.Dispose();
            _sendLock.Dispose();
        }
    }
}
