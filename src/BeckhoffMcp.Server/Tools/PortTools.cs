using System.ComponentModel;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class PortTools
{
    private readonly AdsConnectionManager _ads;
    public PortTools(AdsConnectionManager ads) => _ads = ads;

    [McpServerTool(Name = "beckhoff_query_ads_port"),
     Description("Probe a custom ADS port on the connected target (e.g. 27905 for EK-coupler I/O). Returns device info, state and a small symbol preview.")]
    public async Task<object> QueryAdsPort(
        [Description("ADS port number (e.g. 851 for PLC, 0xFFFF for EtherCAT, 27905 for EK1200)")] int ads_port,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, object?>
        {
            ["ads_port"] = ads_port,
            ["ads_port_hex"] = $"0x{ads_port:X4}",
            ["target_net_id"] = _ads.TargetNetId,
        };

        try
        {
            var conn = _ads.EnsureConnectedToPort(ads_port);

            var info = await conn.ReadDeviceInfoAsync(ct);
            if (info.Succeeded)
                result["device_info"] = new
                {
                    name = info.DeviceInfo.Name,
                    version = $"{info.DeviceInfo.Version.Version}.{info.DeviceInfo.Version.Revision}.{info.DeviceInfo.Version.Build}",
                };
            else result["device_info"] = null;

            var state = await conn.ReadStateAsync(ct);
            if (state.Succeeded)
                result["state"] = new
                {
                    ads_state = state.State.AdsState.ToString(),
                    ads_state_code = (int)state.State.AdsState,
                    device_state = state.State.DeviceState,
                };
            else result["state"] = null;

            try
            {
                var loader = SymbolLoaderFactory.Create(conn, new SymbolLoaderSettings(SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred));
                var syms = loader.Symbols;
                result["symbol_count"] = syms.Count;
                result["symbols_preview"] = syms.Take(20).Select(s => new
                {
                    name = s.InstancePath,
                    type = s.TypeName,
                }).ToList();
            }
            catch
            {
                result["symbol_count"] = 0;
                result["symbols_preview"] = Array.Empty<object>();
            }

            result["success"] = true;
            return result;
        }
        catch (Exception ex)
        {
            result["success"] = false;
            result["error"] = ex.Message;
            result["hint"] = "Verify port. Some devices need 'Enable ADS Server' in TwinCAT XAE.";
            return result;
        }
    }

    [McpServerTool(Name = "beckhoff_read_from_port"),
     Description("Read a symbolic variable from a custom ADS port (typical for I/O couplers like EK1200).")]
    public async Task<object> ReadFromPort(
        [Description("ADS port number")] int ads_port,
        [Description("Full symbol path on that port, e.g. 'Inputs.EL1008.Channel 1.Input'")] string symbol_name,
        CancellationToken ct = default)
    {
        try
        {
            var conn = _ads.EnsureConnectedToPort(ads_port);
            var loader = SymbolLoaderFactory.Create(conn, new SymbolLoaderSettings(SymbolsLoadMode.Flat, TwinCAT.ValueAccess.ValueAccessMode.IndexGroupOffsetPreferred));
            var symbol = loader.Symbols.FirstOrDefault(s =>
                s.InstancePath.Equals(symbol_name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Symbol '{symbol_name}' not found on port {ads_port}");

            var r = await conn.ReadValueAsync(symbol, ct);
            if (r.ErrorCode != TwinCAT.Ads.AdsErrorCode.NoError)
                throw new InvalidOperationException($"Read failed: {r.ErrorCode}");

            return new
            {
                name = symbol.InstancePath,
                type = symbol.TypeName,
                value = r.Value,
                ads_port,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, ads_port, symbol_name };
        }
    }
}
