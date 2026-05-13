using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BeckhoffMcp.Server.Services;

/// <summary>
/// Detects whether a Beckhoff/TwinCAT router is already running locally.
///
/// Two signals, OR-combined:
///   1. TCP port 48898 is bound by some process on the local machine.
///   2. The TwinCAT system service (TcSysSrv) is installed AND running.
///
/// On a system where the in-process <c>AmsTcpIpRouter</c> would collide with
/// an already-installed router, the MCP defers to the installed one. The
/// caller is expected to:
///   - NOT start <c>AmsTcpIpRouter</c>.
///   - NOT override <c>AmsRouter:ChannelProtocol</c> (let Beckhoff.TwinCAT.Ads
///     auto-detect the installed router via PInvoke / UnixSocket / loopback).
///   - Use routes that the installed router already knows about.
///
/// Detection happens once at startup; reinstalling TwinCAT mid-process is
/// not a scenario we support.
/// </summary>
public sealed class LocalRouterDetector
{
    private readonly ILogger<LocalRouterDetector> _log;

    public bool IsPresent { get; }
    public bool Port48898InUse { get; }
    public bool TcSysSrvRunning { get; }
    public string? TcSysSrvStatus { get; }
    public string? InstalledNetId { get; }

    public LocalRouterDetector(ILoggerFactory lf)
    {
        _log = lf.CreateLogger<LocalRouterDetector>();
        Port48898InUse = ProbePort48898();
        (TcSysSrvRunning, TcSysSrvStatus) = ProbeTcSysSrv();
        InstalledNetId = OperatingSystem.IsWindows() ? ReadInstalledNetIdFromRegistry() : null;
        IsPresent = Port48898InUse || TcSysSrvRunning;

        if (IsPresent)
            _log.LogInformation("Local Beckhoff router detected (port 48898 in use: {Port}, TcSysSrv: {Svc}, NetId: {NetId}) — in-process router will be skipped when transport='local' is requested",
                Port48898InUse, TcSysSrvStatus ?? "(unknown)", InstalledNetId ?? "(unknown)");
        else
            _log.LogDebug("No local Beckhoff router detected");
    }

    public string Reason
    {
        get
        {
            if (Port48898InUse && TcSysSrvRunning) return "port_48898_in_use + TcSysSrv running";
            if (Port48898InUse) return "port_48898_in_use";
            if (TcSysSrvRunning) return "TcSysSrv running";
            return "not_detected";
        }
    }

    /// <summary>
    /// True when something is already listening on TCP 48898 on this machine.
    /// Cheap and non-intrusive — we read the active listener table from the OS
    /// rather than trying to bind (which would race) or connect (which would
    /// false-negative if the router isn't yet accepting).
    /// </summary>
    private bool ProbePort48898()
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == 48898) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not enumerate TCP listeners — assuming 48898 is free");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool running, string? status) ProbeTcSysSrvWindows()
    {
        try
        {
            using var sc = new ServiceController("TcSysSrv");
            var status = sc.Status.ToString();
            return (sc.Status == ServiceControllerStatus.Running, status);
        }
        catch
        {
            // Service not installed — exception is the normal signal.
            return (false, null);
        }
    }

    private (bool running, string? status) ProbeTcSysSrv()
    {
        if (!OperatingSystem.IsWindows()) return (false, null);
        return ProbeTcSysSrvWindows();
    }

    /// <summary>
    /// Best-effort read of the installed TwinCAT system's AmsNetId from the
    /// Windows registry. Used as the local NetId when talking through the
    /// installed router so the local route on the PLC matches. Returns null
    /// on failure — caller falls back to the configured/generated NetId.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string? ReadInstalledNetIdFromRegistry()
    {
        try
        {
            // TwinCAT 3 registers as a 32-bit product, so the local NetId
            // lives under the 32-bit registry view even on 64-bit Windows.
            // The value name is "AmsNetId" (lowercase 'd'); some TcXaeMgmt
            // tooling writes it as a 6-byte REG_BINARY, others as a string.
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hive.OpenSubKey(@"SOFTWARE\Beckhoff\TwinCAT3\System");
            var value = key?.GetValue("AmsNetId");
            if (value is byte[] bytes && bytes.Length >= 6)
                return string.Join('.', bytes[..6].Select(b => b.ToString()));
            if (value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read AmsNetId from registry");
        }
        return null;
    }
}
