using System.ComponentModel;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT.Ads;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class DeviceTools
{
    private readonly AdsConnectionManager _ads;
    public DeviceTools(AdsConnectionManager ads) => _ads = ads;

    private IAdsConnection ResolveConnection(int? port) =>
        port is null || port.Value == _ads.TargetPort
            ? _ads.EnsureConnected()
            : _ads.EnsureConnectedToPort(port.Value);

    [McpServerTool(Name = "beckhoff_get_device_info"),
     Description("Read device info (name + version) from the active target. " +
                 "By default queries the active target_port (PLC runtime, e.g. 851). Pass port=10000 to query the " +
                 "SystemService (always responds even when no PLC project is loaded).")]
    public async Task<object> GetDeviceInfo(
        [Description("Optional ADS port override. Defaults to active target_port. Use 10000 for SystemService.")] int? port = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = ResolveConnection(port);
            var result = await conn.ReadDeviceInfoAsync(ct);
            if (!result.Succeeded)
                return new { ok = false, target_net_id = _ads.TargetNetId, port = port ?? _ads.TargetPort, error = result.ErrorCode.ToString() };
            return new
            {
                ok = true,
                target_net_id = _ads.TargetNetId,
                port = port ?? _ads.TargetPort,
                name = result.DeviceInfo.Name,
                version = $"{result.DeviceInfo.Version.Version}.{result.DeviceInfo.Version.Revision}.{result.DeviceInfo.Version.Build}",
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, target_net_id = _ads.TargetNetId, port = port ?? _ads.TargetPort, error = ex.Message };
        }
    }

    [McpServerTool(Name = "beckhoff_get_device_state"),
     Description("Read ADS state (Run/Stop/Config/...) of the active target. " +
                 "By default queries the active target_port (PLC application — e.g. 851 for Plc Runtime 1). " +
                 "Pass port=10000 to query the underlying TwinCAT SystemService — that is the device-level state " +
                 "and always responds even when no PLC project is loaded.")]
    public async Task<object> GetDeviceState(
        [Description("Optional ADS port override. Defaults to active target_port. Use 10000 for the SystemService (device-level) state.")] int? port = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = ResolveConnection(port);
            var result = await conn.ReadStateAsync(ct);
            if (!result.Succeeded)
                return new { ok = false, target_net_id = _ads.TargetNetId, port = port ?? _ads.TargetPort, error = result.ErrorCode.ToString() };
            return new
            {
                ok = true,
                target_net_id = _ads.TargetNetId,
                port = port ?? _ads.TargetPort,
                ads_state = result.State.AdsState.ToString(),
                ads_state_code = (int)result.State.AdsState,
                device_state = result.State.DeviceState,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, target_net_id = _ads.TargetNetId, port = port ?? _ads.TargetPort, error = ex.Message };
        }
    }

    [McpServerTool(Name = "beckhoff_connection_status"),
     Description("Get current connection status and active target. Includes the local AmsNetId — register that NetId on the PLC's StaticRoutes for TCP transport to work.")]
    public object GetConnectionStatus() => new
    {
        connected = _ads.IsConnected,
        target_net_id = _ads.TargetNetId,
        target_port = _ads.TargetPort,
        transport = _ads.ActiveTransport.ToString().ToLowerInvariant(),
        tcp_router_running = _ads.TcpRouterRunning,
        local_net_id = _ads.LocalNetId,
        local_name = _ads.LocalName,
    };
}
