using System.ComponentModel;
using BeckhoffMcp.Server.Services;
using ModelContextProtocol.Server;
using TwinCAT.Ads;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class EthercatTools
{
    private const int EtherCatMasterPort = 0xFFFF; // 65535

    // EtherCAT ADS Index Groups
    private const uint IdxGrpMasterState     = 0x0003;
    private const uint IdxGrpSlaveCount      = 0x0006;
    private const uint IdxGrpSlaveState      = 0x0009;
    private const uint IdxGrpSlaveIdentity   = 0x0011;
    private const uint IdxGrpCrcErrors       = 0x0012;

    private static readonly Dictionary<int, string> MasterStates = new()
    {
        [0] = "Unknown", [1] = "Init", [2] = "PreOp", [3] = "Bootstrap", [4] = "SafeOp", [8] = "Op",
    };

    private static readonly Dictionary<int, string> SlaveStates = new()
    {
        [0x01] = "Init", [0x02] = "PreOp", [0x03] = "Bootstrap",
        [0x04] = "SafeOp", [0x08] = "Op", [0x10] = "Error",
    };

    private static string MasterStateName(int code) =>
        MasterStates.TryGetValue(code, out var n) ? n : $"Unknown({code})";

    private static string SlaveStateName(int code) =>
        SlaveStates.TryGetValue(code & 0x0F, out var n) ? n : $"Unknown({code})";

    private readonly AdsConnectionManager _ads;
    public EthercatTools(AdsConnectionManager ads) => _ads = ads;

    private static object EtherCatPortHint(string error) => new
    {
        error,
        hint = "EtherCAT ADS server may not be enabled. In TwinCAT XAE: " +
               "double-click 'Image' under EtherCAT Master, go to 'ADS' tab, " +
               "enable 'Enable ADS Server' and 'Create symbols', then activate config.",
        possible_causes = new[]
        {
            "No EtherCAT master configured on this PLC",
            "ADS Server not enabled for EtherCAT device",
            "Remote ADS access to EtherCAT may be restricted",
        },
    };

    [McpServerTool(Name = "beckhoff_get_ethercat_master_state"),
     Description("Get the EtherCAT master state (Init/PreOp/SafeOp/Op).")]
    public async Task<object> GetMasterState(CancellationToken ct = default)
    {
        try
        {
            var ec = _ads.EnsureConnectedToPort(EtherCatMasterPort);
            var r = await ec.ReadAnyAsync(IdxGrpMasterState, 0x0000, typeof(ushort), ct);
            if (r.ErrorCode != AdsErrorCode.NoError) return EtherCatPortHint($"ReadMasterState: {r.ErrorCode}");
            var state = Convert.ToInt32(r.Value ?? (ushort)0);
            return new
            {
                master_state = new { code = state, name = MasterStateName(state) },
                target_net_id = _ads.TargetNetId,
                ethercat_port = EtherCatMasterPort,
            };
        }
        catch (Exception ex) { return EtherCatPortHint(ex.Message); }
    }

    [McpServerTool(Name = "beckhoff_get_ethercat_slave_count"),
     Description("Get the number of configured EtherCAT slaves.")]
    public async Task<object> GetSlaveCount(CancellationToken ct = default)
    {
        try
        {
            var ec = _ads.EnsureConnectedToPort(EtherCatMasterPort);
            var r = await ec.ReadAnyAsync(IdxGrpSlaveCount, 0x0000, typeof(uint), ct);
            if (r.ErrorCode != AdsErrorCode.NoError) return EtherCatPortHint($"ReadSlaveCount: {r.ErrorCode}");
            return new
            {
                slave_count = Convert.ToUInt32(r.Value ?? 0u),
                target_net_id = _ads.TargetNetId,
            };
        }
        catch (Exception ex) { return EtherCatPortHint(ex.Message); }
    }

    [McpServerTool(Name = "beckhoff_get_ethercat_topology"),
     Description("Iterate all configured EtherCAT slaves: state + identity per slave.")]
    public async Task<object> GetTopology(CancellationToken ct = default)
    {
        try
        {
            var ec = _ads.EnsureConnectedToPort(EtherCatMasterPort);
            var rCount = await ec.ReadAnyAsync(IdxGrpSlaveCount, 0x0000, typeof(uint), ct);
            if (rCount.ErrorCode != AdsErrorCode.NoError)
                return EtherCatPortHint($"ReadSlaveCount: {rCount.ErrorCode}");
            var count = Convert.ToInt32(rCount.Value ?? 0u);

            var slaves = new List<object>();
            for (var addr = 1; addr <= count; addr++)
                slaves.Add(await ReadSlaveSummary(ec, (uint)addr, ct));

            return new { slave_count = count, slaves, target_net_id = _ads.TargetNetId };
        }
        catch (Exception ex) { return EtherCatPortHint(ex.Message); }
    }

    [McpServerTool(Name = "beckhoff_get_ethercat_slave_info"),
     Description("Detailed info for one EtherCAT slave (state, identity, CRC errors).")]
    public async Task<object> GetSlaveInfo(
        [Description("EtherCAT slave address (1-indexed)")] int slave_address,
        CancellationToken ct = default)
    {
        if (slave_address < 1) return new { error = "slave_address must be >= 1" };
        try
        {
            var ec = _ads.EnsureConnectedToPort(EtherCatMasterPort);
            var addr = (uint)slave_address;
            var summary = (Dictionary<string, object?>)await ReadSlaveSummary(ec, addr, ct, asDict: true);

            var crcResult = await ec.ReadAnyAsync(IdxGrpCrcErrors, addr, typeof(uint), ct);
            if (crcResult.ErrorCode == AdsErrorCode.NoError)
                summary["crc_errors"] = Convert.ToUInt32(crcResult.Value ?? 0u);

            summary["target_net_id"] = _ads.TargetNetId;
            return summary;
        }
        catch (Exception ex) { return EtherCatPortHint(ex.Message); }
    }

    private static async Task<object> ReadSlaveSummary(
        IAdsConnection ec, uint addr, CancellationToken ct, bool asDict = false)
    {
        var dict = new Dictionary<string, object?> { ["address"] = (int)addr };

        var rState = await ec.ReadAnyAsync(IdxGrpSlaveState, addr, typeof(ushort), ct);
        if (rState.ErrorCode == AdsErrorCode.NoError)
        {
            var s = Convert.ToInt32(rState.Value ?? (ushort)0);
            dict["state"] = new { code = s, name = SlaveStateName(s) };
        }
        else dict["state_error"] = rState.ErrorCode.ToString();

        // Identity = vendor ID (4) + product code (4)
        var identityBuf = new byte[8];
        var rIdentity = await ec.ReadAsync(IdxGrpSlaveIdentity, addr, identityBuf.AsMemory(), ct);
        if (rIdentity.ErrorCode == AdsErrorCode.NoError && rIdentity.ReadBytes >= 8)
        {
            dict["vendor_id"] = BitConverter.ToUInt32(identityBuf, 0);
            dict["product_code"] = BitConverter.ToUInt32(identityBuf, 4);
        }

        return asDict ? dict : (object)dict;
    }
}
