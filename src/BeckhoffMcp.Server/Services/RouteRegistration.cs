using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Native;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Talks the Beckhoff "ADS-Discovery" protocol on UDP/48899 — the same wire
/// format that XAE's "Add Route Dialog" uses. Beckhoff exposes the constants
/// (UdpDiscoveryServiceID, UdpDiscoveryTagID) but no high-level client, so we
/// drive the bytes ourselves.
///
/// Wire format (24-byte header + tags, little-endian):
///   [0..3]   magic cookie 0x71146603 (raw bytes 03 66 14 71)
///   [4..7]   invokeId (0)
///   [8..11]  serviceId (high bit set in replies = response)
///   [12..17] sender AmsNetId (6 bytes)
///   [18..19] sender port (uint16)
///   [20..23] tagCount (uint32)
///   tag[]:   tagId(uint16) length(uint16) payload[length]
/// </summary>
public sealed class RouteRegistration
{
    private const uint MagicCookie = 0x71146603;
    private const ushort UdpPort = 48899;

    private const uint SrvServerInfo = 1;
    private const uint SrvAddRoute   = 6;
    private const uint SrvReadRoutes = 8;

    // Wire tag IDs (16-bit on the wire). Layout follows Beckhoff's
    // UdpDiscoveryTagFactory: NetId/IPAddress are raw bytes, RouteName /
    // UserName / Password are NUL-terminated ASCII strings.
    private const ushort TagResult       = 1;
    private const ushort TagPassword     = 2;
    private const ushort TagComputerName = 5;
    private const ushort TagNetId        = 7;
    private const ushort TagIPAddress    = 8;
    private const ushort TagTemporary    = 9;
    private const ushort TagRouteName    = 12;
    private const ushort TagUserName     = 13;
    private const ushort TagPasswordAes  = 14;
    private const ushort TagUsernameAes  = 15;

    private readonly ILogger<RouteRegistration> _log;
    public RouteRegistration(ILoggerFactory lf) => _log = lf.CreateLogger<RouteRegistration>();

    /// <summary>
    /// Sends an AddRoute request to <paramref name="targetIp"/>. Result tag in
    /// the reply is non-zero on auth failure / already-exists / unsupported.
    /// </summary>
    public async Task<RouteAddResult> AddRouteAsync(
        string targetIp,
        AmsNetId targetNetId,
        AmsNetId localNetId,
        string localIp,
        string routeName,
        string username,
        string password,
        bool temporary,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        // Mirrors Beckhoff/ADS adstool's AddRemoteRoute (AdsLib/AdsLib.cpp).
        // Note: NetId tag is OUR netId (this is the route we're registering),
        // ComputerName tag is the TARGET's address — used by the PLC for
        // labelling the registered route.
        var packet = BuildAddRoutePacket(localNetId, localNetId, targetIp, routeName, username, password, temporary);
        _log.LogInformation("AddRoute → {Ip}: {Bytes} bytes\n  TX: {Hex}",
            targetIp, packet.Length, BitConverter.ToString(packet).Replace("-", ""));
        var reply = await SendAndReceiveAsync(targetIp, packet, timeout, ct);
        if (reply is not null)
            _log.LogInformation("AddRoute reply ({Bytes} bytes): {Hex}",
                reply.Length, BitConverter.ToString(reply).Replace("-", ""));
        if (reply is null)
            return new RouteAddResult(false, "no_reply", "PLC did not answer on UDP/48899 within timeout.");

        var (serviceId, tags) = ParseUdp(reply);
        if ((serviceId & 0x7FFFFFFF) != SrvAddRoute)
            return new RouteAddResult(false, "wrong_reply", $"Unexpected service id 0x{serviceId:x8}");

        var resultTag = tags.FirstOrDefault(t => t.id == TagResult).bytes;
        if (resultTag is null || resultTag.Length < 4)
            return new RouteAddResult(true, "ok_no_result_tag", "PLC accepted (no Result tag returned).");

        var code = BitConverter.ToUInt32(resultTag, 0);
        return code switch
        {
            0 => new RouteAddResult(true, "ok", "Route added."),
            1804 => new RouteAddResult(false, "auth_failed", "Authentication failed (wrong username/password)."),
            _ => new RouteAddResult(false, $"error_{code}", $"PLC returned error 0x{code:x8} ({code})."),
        };
    }

    /// <summary>
    /// Reads the route list from a target and tells us whether a route for
    /// <paramref name="ourNetId"/> already exists. Used to skip the credential
    /// prompt when the route is already there.
    /// </summary>
    public async Task<RouteCheckResult> RouteExistsAsync(
        string targetIp, AmsNetId ourNetId, TimeSpan timeout, CancellationToken ct = default)
    {
        var packet = BuildHeaderOnly(SrvReadRoutes, ourNetId, port: 10000);
        var reply = await SendAndReceiveAsync(targetIp, packet, timeout, ct);
        if (reply is null)
            return new RouteCheckResult(false, false, "no_reply");

        var (serviceId, _) = ParseUdp(reply);
        if (((UdpDiscoveryServiceID)(serviceId & 0x7FFF)) != UdpDiscoveryServiceID.ReadRoutes)
            return new RouteCheckResult(false, false, $"wrong_service_0x{serviceId:x8}");

        // The reply payload contains the NetId of every registered route
        // somewhere in its bytes. A byte-needle search is reliable enough.
        var found = ContainsNetId(reply, ourNetId);
        return new RouteCheckResult(true, found, found ? "exists" : "missing");
    }


    /// <summary>
    /// Cheap reachability probe — sends ServerInfo, reads the answer. Used to
    /// detect "PLC IP wrong" before we ask the user for credentials.
    /// </summary>
    public async Task<bool> ReachableAsync(string targetIp, TimeSpan timeout, CancellationToken ct = default)
    {
        var pkt = BuildHeaderOnly(SrvServerInfo, AmsNetId.Empty, port: 0);
        return await SendAndReceiveAsync(targetIp, pkt, timeout, ct) is not null;
    }

    // ---------------- Wire format helpers ----------------

    private static byte[] BuildHeaderOnly(uint serviceId, AmsNetId src, ushort port)
    {
        var ms = new MemoryStream(32);
        var bw = new BinaryWriter(ms);
        bw.Write(MagicCookie);          // 4 bytes — magic
        bw.Write((uint)0);              // 4 bytes — invokeId
        bw.Write(serviceId);            // 4 bytes — serviceId
        bw.Write(NetIdBytes(src));      // 6 bytes — src NetId
        bw.Write(port);                 // 2 bytes — src port
        bw.Write((uint)0);              // 4 bytes — tagCount
        return ms.ToArray();
    }

    private static byte[] BuildAddRoutePacket(
        AmsNetId src, AmsNetId routedNetId, string targetAddress, string routeName,
        string username, string password, bool temporary)
    {
        var ms = new MemoryStream(256);
        var bw = new BinaryWriter(ms);
        bw.Write(MagicCookie);
        bw.Write((uint)0);              // invokeId
        bw.Write(SrvAddRoute);
        bw.Write(NetIdBytes(src));      // 6 bytes
        bw.Write((ushort)10000);        // src port

        // Tag order from Beckhoff/ADS AdsLib/AdsLib.cpp::AddRemoteRoute. The
        // C++ side prepends in reverse, so on the wire the order is:
        // RouteName, NetId, UserName, Password, ComputerName.
        // ComputerName carries the TARGET's address (used by the PLC to label
        // the registered route). The Temporary tag (uint32 = 1) tells the PLC
        // not to persist this route across reboots — perfect for an MCP that
        // shouldn't litter the StaticRoutes.xml.
        var tagBlock = new MemoryStream(192);
        var tw = new BinaryWriter(tagBlock);
        WriteTagString(tw, TagRouteName,    routeName);
        WriteTagBytes (tw, TagNetId,        NetIdBytes(routedNetId));
        WriteTagString(tw, TagUserName,     username);
        WriteTagString(tw, TagPassword,     password ?? string.Empty);
        WriteTagString(tw, TagComputerName, targetAddress);
        if (temporary)
            WriteTagBytes(tw, TagTemporary, BitConverter.GetBytes((uint)1));

        var tagBytes = tagBlock.ToArray();
        bw.Write((uint)(temporary ? 6 : 5));   // tagCount
        bw.Write(tagBytes);
        return ms.ToArray();
    }

    private static void WriteTagString(BinaryWriter bw, ushort tagId, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var withNul = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, withNul, 0, bytes.Length);
        WriteTagBytes(bw, tagId, withNul);
    }

    private static void WriteTagBytes(BinaryWriter bw, ushort tagId, byte[] payload)
    {
        bw.Write(tagId);                       // 2 bytes
        bw.Write((ushort)payload.Length);      // 2 bytes
        bw.Write(payload);
    }

    private static byte[] NetIdBytes(AmsNetId id)
    {
        if (id == AmsNetId.Empty || id is null) return new byte[6];
        var parts = id.ToString().Split('.');
        if (parts.Length != 6) throw new ArgumentException($"AmsNetId not 6-tuple: {id}");
        var b = new byte[6];
        for (int i = 0; i < 6; i++) b[i] = byte.Parse(parts[i]);
        return b;
    }

    private static (uint serviceId, List<(ushort id, byte[] bytes)> tags) ParseUdp(byte[] data)
    {
        var tags = new List<(ushort, byte[])>();
        if (data.Length < 24) return (0, tags);
        uint cookie = BitConverter.ToUInt32(data, 0);
        if (cookie != MagicCookie) return (0, tags);
        // [4..7] invokeId  [8..11] serviceId  [12..17] srcNetId  [18..19] port  [20..23] tagCount
        uint sid = BitConverter.ToUInt32(data, 8);
        uint tagCount = BitConverter.ToUInt32(data, 20);
        int offset = 24;
        for (uint i = 0; i < tagCount && offset + 4 <= data.Length; i++)
        {
            ushort tid = BitConverter.ToUInt16(data, offset);
            ushort len = BitConverter.ToUInt16(data, offset + 2);
            offset += 4;
            if (offset + len > data.Length) break;
            var payload = new byte[len];
            Buffer.BlockCopy(data, offset, payload, 0, len);
            tags.Add((tid, payload));
            offset += len;
        }
        return (sid, tags);
    }

    private static bool ContainsNetId(byte[] data, AmsNetId netId)
    {
        var needle = NetIdBytes(netId);
        for (int i = 0; i + needle.Length <= data.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private async Task<byte[]?> SendAndReceiveAsync(string targetIp, byte[] packet,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!IPAddress.TryParse(targetIp, out var targetAddr))
        {
            _log.LogWarning("Invalid IP for UDP route op: {Ip}", targetIp);
            return null;
        }

        using var udp = new UdpClient(0);
        udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        udp.Client.SendTimeout    = (int)timeout.TotalMilliseconds;

        var endpoint = new IPEndPoint(targetAddr, UdpPort);
        await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var task = udp.ReceiveAsync();
            var done = await Task.WhenAny(task, Task.Delay(timeout, cts.Token));
            if (done == task) return task.Result.Buffer;
            return null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UDP route op did not get a reply");
            return null;
        }
    }

    public sealed record RouteAddResult(bool Success, string Code, string Message);
    public sealed record RouteCheckResult(bool Reachable, bool Exists, string Code);
}
