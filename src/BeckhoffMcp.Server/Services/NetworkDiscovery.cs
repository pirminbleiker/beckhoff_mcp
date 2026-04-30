using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Network-level discovery for Beckhoff devices: UDP 48899 ADS probe and TCP
/// port scan for the well-known Beckhoff/Industrial ports. Designed to run
/// against a CIDR subnet or an explicit list of IPs in parallel.
/// </summary>
public sealed class NetworkDiscovery
{
    private readonly ILogger<NetworkDiscovery> _log;
    public NetworkDiscovery(ILogger<NetworkDiscovery> log) => _log = log;

    public sealed record AdsResponse(
        string IpAddress,
        string AmsNetId,
        int AmsPort,
        string? HostName,
        string? OsName,
        string? TwinCatVersion,
        string? Fingerprint);

    public sealed record TcpProbeResult(int Port, string Label, bool Open);

    public sealed record HostResult(
        string IpAddress,
        AdsResponse? Ads,
        IReadOnlyList<TcpProbeResult> OpenPorts,
        long ElapsedMs);

    /// <summary>Common Beckhoff/IPC TCP ports we probe.</summary>
    public static readonly IReadOnlyDictionary<int, string> KnownTcpPorts = new SortedDictionary<int, string>
    {
        [22] = "SSH (TwinCAT/BSD, Linux IPC)",
        [80] = "HTTP (Web UI / Device Manager)",
        [443] = "HTTPS (Device Manager TLS)",
        [3389] = "RDP (Windows IPC)",
        [5900] = "VNC",
        [5120] = "TwinCAT Management Console",
        [1883] = "MQTT",
        [8883] = "MQTT/TLS",
        [8016] = "Secure ADS (TLS)",
        [48898] = "ADS / AMS over TCP",
        [34980] = "EtherCAT Automation Protocol",
    };

    /// <summary>Auto-detect candidate subnets from local IPv4 interfaces.</summary>
    public static IReadOnlyList<string> AutoDetectSubnets()
    {
        var subnets = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254."))
                    subnets.Add("169.254.0.0/16");
                else if (ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
                         (ip.StartsWith("172.") && IsPrivate172(ip)))
                {
                    var parts = ip.Split('.');
                    subnets.Add($"{parts[0]}.{parts[1]}.{parts[2]}.0/24");
                }
            }
        }
        return subnets.ToList();
    }

    private static bool IsPrivate172(string ip)
    {
        var second = int.Parse(ip.Split('.')[1]);
        return second >= 16 && second <= 31;
    }

    public static IEnumerable<string> ExpandSubnet(string cidr)
    {
        var parts = cidr.Split('/');
        var baseIp = IPAddress.Parse(parts[0]);
        var prefix = int.Parse(parts[1]);
        var bytes = baseIp.GetAddressBytes();
        Array.Reverse(bytes);
        var baseInt = BitConverter.ToUInt32(bytes, 0);
        var hostBits = 32 - prefix;
        if (hostBits is < 1 or > 16)
            throw new ArgumentException($"Subnet /{prefix} too narrow or too wide (limited to /16-/31)");
        var count = 1u << hostBits;
        var mask = hostBits >= 32 ? 0u : (uint.MaxValue << hostBits);
        var network = baseInt & mask;
        for (uint i = 1; i < count - 1; i++)
        {
            var ipInt = network + i;
            var ipBytes = BitConverter.GetBytes(ipInt);
            Array.Reverse(ipBytes);
            yield return new IPAddress(ipBytes).ToString();
        }
    }

    /// <summary>Send UDP 48899 ADS discovery packet, parse response.</summary>
    public async Task<AdsResponse?> ProbeAdsAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        // Magic cookie: 0x03 0x66 0x14 0x71 (Beckhoff UDP ADS discovery)
        var packet = new byte[24];
        packet[0] = 0x03; packet[1] = 0x66; packet[2] = 0x14; packet[3] = 0x71;
        // bytes 4-7: zero
        packet[8] = 0x01; // service ID 1 = ServerInfo
        // 9-11 zero
        packet[12] = 1; packet[13] = 1; packet[14] = 1; packet[15] = 1; packet[16] = 1; packet[17] = 1; // sender NetId
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18, 2), 10000); // sender port
        // bytes 20-23 zero (trailer)

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), 48899);
            await udp.SendAsync(packet, packet.Length, endpoint).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var receiveTask = udp.ReceiveAsync(cts.Token).AsTask();
            var result = await receiveTask;
            return ParseAdsReply(ip, result.Buffer);
        }
        catch
        {
            return null;
        }
    }

    private static AdsResponse? ParseAdsReply(string ip, byte[] reply)
    {
        if (reply.Length < 24) return null;
        var netId = $"{reply[12]}.{reply[13]}.{reply[14]}.{reply[15]}.{reply[16]}.{reply[17]}";
        var amsPort = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(18, 2));

        string? hostname = null, tcVersion = null, osName = null, fingerprint = null;
        var i = 24;
        while (i + 4 <= reply.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(i, 2)); i += 2;
            var len = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(i, 2)); i += 2;
            if (i + len > reply.Length) break;
            var data = reply.AsSpan(i, len).ToArray();
            i += len;

            switch (tag)
            {
                case 5: // hostname
                    hostname = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                    break;
                case 3: // TwinCAT version: major(1) minor(1) build(2 LE)
                    if (data.Length >= 4)
                    {
                        var build = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
                        tcVersion = $"{data[0]}.{data[1]}.{build}";
                    }
                    break;
                case 4: // OS name (printable ASCII embedded)
                    osName = ExtractAsciiRun(data);
                    break;
                case 18: // fingerprint
                    fingerprint = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                    break;
            }
        }

        return new AdsResponse(ip, netId, amsPort, hostname, osName, tcVersion, fingerprint);
    }

    private static string? ExtractAsciiRun(byte[] data)
    {
        var start = -1;
        for (var k = 0; k < data.Length - 3; k++)
        {
            if (data[k] >= 0x41 && data[k] <= 0x7a &&
                data[k + 1] >= 0x20 && data[k + 1] <= 0x7e &&
                data[k + 2] >= 0x20 && data[k + 2] <= 0x7e)
            {
                start = k; break;
            }
        }
        if (start < 0) return null;
        var end = start;
        while (end < data.Length && data[end] >= 0x20 && data[end] <= 0x7e) end++;
        return System.Text.Encoding.ASCII.GetString(data, start, end - start);
    }

    /// <summary>Connect-with-timeout TCP probe.</summary>
    public static async Task<bool> ProbeTcpAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Probe a single host: UDP ADS + all known TCP ports.</summary>
    public async Task<HostResult> ProbeHostAsync(string ip, int udpTimeoutMs, int tcpTimeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ads = await ProbeAdsAsync(ip, udpTimeoutMs, ct);

        var portTasks = KnownTcpPorts.Select(async kv =>
        {
            var open = await ProbeTcpAsync(ip, kv.Key, tcpTimeoutMs, ct);
            return new TcpProbeResult(kv.Key, kv.Value, open);
        }).ToArray();
        var ports = await Task.WhenAll(portTasks);
        return new HostResult(ip, ads, ports.Where(p => p.Open).ToList(), sw.ElapsedMilliseconds);
    }

    /// <summary>Probe a list of IPs in parallel; only return those that responded with ADS or any open TCP port.</summary>
    public async Task<IReadOnlyList<HostResult>> ProbeRangeAsync(
        IEnumerable<string> ips, int udpTimeoutMs, int tcpTimeoutMs,
        int maxParallelism, CancellationToken ct)
    {
        var results = new ConcurrentBag<HostResult>();
        var sem = new SemaphoreSlim(maxParallelism);
        var tasks = ips.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await ProbeHostAsync(ip, udpTimeoutMs, tcpTimeoutMs, ct);
                if (r.Ads is not null || r.OpenPorts.Count > 0)
                    results.Add(r);
            }
            finally { sem.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.IpAddress, StringComparer.Ordinal).ToList();
    }
}
