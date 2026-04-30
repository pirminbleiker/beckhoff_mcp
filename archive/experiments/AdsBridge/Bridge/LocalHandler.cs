using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.AdsBridge.Bridge;

/// <summary>
/// Handles AMS frames addressed to the bridge's own NetId. Replies with sane
/// defaults for the common router-internal commands so well-behaved clients
/// (pyads / TwinCAT tools) don't see TargetMachineNotFound when they ping us.
/// </summary>
public sealed class LocalHandler
{
    private readonly ILogger<LocalHandler> _log;
    private readonly RouteTable _routes;

    public LocalHandler(RouteTable routes, ILogger<LocalHandler> log)
    {
        _routes = routes;
        _log = log;
    }

    public bool IsForUs(AmsFrame frame) => frame.TargetNetId.Equals(_routes.LocalNetId);

    /// <summary>Builds a response frame for a request addressed to our own NetId.</summary>
    public AmsFrame Handle(AmsFrame request)
    {
        var (errorCode, payload) = HandleCommand(request);

        return new AmsFrame
        {
            TargetNetId = request.SourceNetId,
            TargetPort = request.SourcePort,
            SourceNetId = request.TargetNetId,
            SourcePort = request.TargetPort,
            CommandId = request.CommandId,
            StateFlags = (ushort)(request.StateFlags | 0x01),
            ErrorCode = errorCode,
            InvokeId = request.InvokeId,
            Payload = payload,
        };
    }

    private (uint errorCode, byte[] payload) HandleCommand(AmsFrame req)
    {
        // CommandId reference: 1=ReadDeviceInfo, 2=Read, 3=Write, 4=ReadState,
        //                     5=WriteControl, 6=AddDeviceNotification,
        //                     7=DelDeviceNotification, 8=DeviceNotification, 9=ReadWrite
        switch (req.CommandId)
        {
            case 1: return DeviceInfoResponse();
            case 4: return ReadStateResponse();
            case 9 when req.TargetPort == 10000: return SystemServiceReadWriteResponse(req);
            default:
                _log.LogDebug("LocalHandler: no handler for cmd={Cmd} port={Port}, replying NOERR empty",
                    req.CommandId, req.TargetPort);
                // Empty success response so we don't trip up well-behaved clients
                return (0, Array.Empty<byte>());
        }
    }

    private (uint, byte[]) DeviceInfoResponse()
    {
        // Layout: result(4) + version(major byte, minor byte, build u16) + name[16]
        var buf = new byte[4 + 4 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0); // result NOERR
        buf[4] = 1;  // version major
        buf[5] = 0;  // version minor
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), 0); // build
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(_routes.LocalName);
        var copyLen = Math.Min(nameBytes.Length, 15);
        Array.Copy(nameBytes, 0, buf, 8, copyLen);
        return (0, buf);
    }

    private (uint, byte[]) ReadStateResponse()
    {
        // Layout: result(4) + adsState u16 + deviceState u16
        var buf = new byte[4 + 2 + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), 5); // AdsState.Run
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), 0);
        return (0, buf);
    }

    private (uint, byte[]) SystemServiceReadWriteResponse(AmsFrame req)
    {
        // ReadWrite request layout: indexGroup(4) + indexOffset(4) + readLen(4) + writeLen(4) + data[writeLen]
        if (req.Payload.Length < 16) return (0x710, Array.Empty<byte>()); // bad request
        var indexGroup = BinaryPrimitives.ReadUInt32LittleEndian(req.Payload.AsSpan(0, 4));
        _log.LogDebug("SystemService ReadWrite indexGroup=0x{IG:X}", indexGroup);
        // Reply: result(4) + readLen(4) + data[readLen] — empty success
        var buf = new byte[8];
        return (0, buf);
    }
}
