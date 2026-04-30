using System.Buffers.Binary;

namespace BeckhoffMcp.AdsBridge.Bridge;

/// <summary>
/// AMS NetId: 6 bytes, classically derived from IP plus 2 trailing.
/// </summary>
public readonly record struct AmsNetId(byte B1, byte B2, byte B3, byte B4, byte B5, byte B6)
{
    public override string ToString() => $"{B1}.{B2}.{B3}.{B4}.{B5}.{B6}";

    public static AmsNetId Parse(string s)
    {
        var p = s.Split('.');
        if (p.Length != 6) throw new FormatException($"Invalid AmsNetId '{s}'");
        return new AmsNetId(
            byte.Parse(p[0]), byte.Parse(p[1]), byte.Parse(p[2]),
            byte.Parse(p[3]), byte.Parse(p[4]), byte.Parse(p[5]));
    }

    public byte[] ToBytes() => new[] { B1, B2, B3, B4, B5, B6 };

    public static AmsNetId FromBytes(ReadOnlySpan<byte> b) =>
        new(b[0], b[1], b[2], b[3], b[4], b[5]);
}

/// <summary>
/// AMS-TCP framing: a 6-byte header (2 reserved + 4 length) prefixes each AMS packet on TCP-Loopback.
/// AMS-Header is 32 bytes followed by payload.
/// Total: AMS-TCP-Header (6) + AMS-Header (32) + Payload(N).
/// </summary>
public sealed class AmsFrame
{
    public const int AmsTcpHeaderSize = 6;
    public const int AmsHeaderSize = 32;

    public AmsNetId TargetNetId { get; init; }
    public ushort TargetPort { get; init; }
    public AmsNetId SourceNetId { get; init; }
    public ushort SourcePort { get; init; }
    public ushort CommandId { get; init; }
    public ushort StateFlags { get; init; }
    public uint ErrorCode { get; init; }
    public uint InvokeId { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public bool IsResponse => (StateFlags & 0x01) != 0;
    public bool IsRequest => !IsResponse;

    /// <summary>Reads one full frame from the stream.</summary>
    public static async Task<AmsFrame?> ReadAsync(Stream s, CancellationToken ct)
    {
        var hdr = new byte[AmsTcpHeaderSize];
        if (!await ReadExactAsync(s, hdr, ct)) return null;

        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(2, 4));
        if (length < AmsHeaderSize) throw new InvalidDataException($"AMS length too small: {length}");
        if (length > 16 * 1024 * 1024) throw new InvalidDataException($"AMS length too big: {length}");

        var body = new byte[length];
        if (!await ReadExactAsync(s, body, ct)) return null;

        var span = body.AsSpan();
        var target = AmsNetId.FromBytes(span.Slice(0, 6));
        var targetPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
        var source = AmsNetId.FromBytes(span.Slice(8, 6));
        var sourcePort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2));
        var cmdId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16, 2));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(18, 2));
        var dataLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        var errCode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        var invokeId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));

        var payloadStart = AmsHeaderSize;
        var payloadLen = (int)Math.Min(dataLen, length - AmsHeaderSize);
        var payload = body.AsSpan(payloadStart, payloadLen).ToArray();

        return new AmsFrame
        {
            TargetNetId = target,
            TargetPort = targetPort,
            SourceNetId = source,
            SourcePort = sourcePort,
            CommandId = cmdId,
            StateFlags = flags,
            ErrorCode = errCode,
            InvokeId = invokeId,
            Payload = payload,
        };
    }

    /// <summary>Writes this frame as AMS-TCP packet to the stream.</summary>
    public async Task WriteAsync(Stream s, CancellationToken ct)
    {
        var totalAmsLen = (uint)(AmsHeaderSize + Payload.Length);
        var buf = new byte[AmsTcpHeaderSize + totalAmsLen];

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), totalAmsLen);

        var b = buf.AsSpan(AmsTcpHeaderSize);
        TargetNetId.ToBytes().CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(6, 2), TargetPort);
        SourceNetId.ToBytes().CopyTo(b.Slice(8));
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(14, 2), SourcePort);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(16, 2), CommandId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(18, 2), StateFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(20, 4), (uint)Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(24, 4), ErrorCode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(28, 4), InvokeId);
        Payload.CopyTo(b.Slice(AmsHeaderSize));

        await s.WriteAsync(buf, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>Encodes this frame as a single byte buffer (without the AMS-TCP outer header) for MQTT payload.</summary>
    public byte[] ToAmsBytes()
    {
        var buf = new byte[AmsHeaderSize + Payload.Length];
        var b = buf.AsSpan();
        TargetNetId.ToBytes().CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(6, 2), TargetPort);
        SourceNetId.ToBytes().CopyTo(b.Slice(8));
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(14, 2), SourcePort);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(16, 2), CommandId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(18, 2), StateFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(20, 4), (uint)Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(24, 4), ErrorCode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(28, 4), InvokeId);
        Payload.CopyTo(b.Slice(AmsHeaderSize));
        return buf;
    }

    /// <summary>Parses an AMS frame from a raw buffer (no AMS-TCP outer header).</summary>
    public static AmsFrame FromAmsBytes(byte[] body)
    {
        var span = body.AsSpan();
        if (span.Length < AmsHeaderSize) throw new InvalidDataException("AMS payload too small");
        var target = AmsNetId.FromBytes(span.Slice(0, 6));
        var targetPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
        var source = AmsNetId.FromBytes(span.Slice(8, 6));
        var sourcePort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2));
        var cmdId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16, 2));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(18, 2));
        var dataLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        var errCode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        var invokeId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));
        var payloadLen = (int)Math.Min(dataLen, body.Length - AmsHeaderSize);
        var payload = body.AsSpan(AmsHeaderSize, payloadLen).ToArray();
        return new AmsFrame
        {
            TargetNetId = target,
            TargetPort = targetPort,
            SourceNetId = source,
            SourcePort = sourcePort,
            CommandId = cmdId,
            StateFlags = flags,
            ErrorCode = errCode,
            InvokeId = invokeId,
            Payload = payload,
        };
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public override string ToString() =>
        $"{SourceNetId}:{SourcePort} → {TargetNetId}:{TargetPort} cmd={CommandId} flags=0x{StateFlags:X4} invokeId={InvokeId} payload={Payload.Length}b";
}
