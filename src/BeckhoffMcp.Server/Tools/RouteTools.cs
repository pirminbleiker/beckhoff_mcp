using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using BeckhoffMcp.Server.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TwinCAT.Ads;

namespace BeckhoffMcp.Server.Tools;

[McpServerToolType]
public sealed class RouteTools
{
    private readonly AdsConnectionManager _ads;
    private readonly RouteRegistration _route;
    private readonly WindowsCredentialPrompt _cred;
    private readonly ILogger<RouteTools> _log;

    public RouteTools(AdsConnectionManager ads, RouteRegistration route,
        WindowsCredentialPrompt cred, ILoggerFactory lf)
    {
        _ads = ads;
        _route = route;
        _cred = cred;
        _log = lf.CreateLogger<RouteTools>();
    }

    [McpServerTool(Name = "beckhoff_add_route"),
     Description("Register our local AmsNetId as a route on the given PLC over UDP/48899 — same wire format as XAE's 'Add Route Dialog'. " +
                 "By default registers a TEMPORARY route (gone on PLC reboot) so the MCP doesn't litter StaticRoutes.xml. " +
                 "Set temporary=false for a persistent entry. " +
                 "When credentials are missing pops the standard Windows credential dialog (same as RDP); saved credentials live in the Windows Credential Manager keyed by the target IP. " +
                 "Required for TCP transport — otherwise the PLC silently drops AMS frames.")]
    [SupportedOSPlatform("windows")]
    public async Task<object> AddRoute(
        [Description("AmsNetId of the target PLC (e.g. '169.254.34.222.1.1').")] string target_net_id,
        [Description("IP address of the target PLC (e.g. '192.168.71.38').")] string target_ip,
        [Description("Optional name for the route entry on the PLC. Defaults to 'BeckhoffMcp-<localNetId>'.")] string? route_name = null,
        [Description("Register as a temporary route — gone after the PLC reboots. Default true (the MCP normally doesn't want to persist anything on the target).")] bool temporary = true,
        [Description("Force credential prompt even when a saved credential exists. Default false.")] bool force_prompt = false,
        [Description("Dry run — only check reachability; never prompt or send credentials. Default false.")] bool dry_run = false,
        [Description("Probe / send timeout (ms). Default 4000.")] int timeout_ms = 4000,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new { success = false, error = "beckhoff_add_route currently requires Windows (uses CredUI / Credential Manager)." };

        if (!AmsNetId.TryParse(target_net_id, out var targetNet))
            return new { success = false, error = $"Invalid AmsNetId: {target_net_id}" };
        if (!AmsNetId.TryParse(_ads.LocalNetId, out var localNet))
            return new { success = false, error = $"Local AmsNetId '{_ads.LocalNetId}' is not parseable." };

        var localIp = PickLocalIp(target_ip);
        if (localIp is null)
            return new { success = false, error = $"Could not pick a local IP that can reach {target_ip}." };

        var name = route_name ?? $"BeckhoffMcp-{_ads.LocalNetId}";
        var timeout = TimeSpan.FromMilliseconds(Math.Max(500, timeout_ms));

        // 1) Reachability check — fail fast if the PLC's UDP service is down.
        var reachable = await _route.ReachableAsync(target_ip, timeout, ct);
        if (!reachable)
            return new
            {
                success = false,
                error = $"PLC at {target_ip} did not answer on UDP/48899.",
                hint = "Run beckhoff_discover_network first; verify the IP is correct and the firewall allows UDP/48899 inbound on the PLC.",
            };

        // 2) Best-effort exists-check — only short-circuits when ReadRoutes
        //    works without auth (older / Linux targets). On TC ≥ 4024 with
        //    auth enforced this returns "exists=false" and we fall through to
        //    AddRoute (which is idempotent — re-adding an existing route is a
        //    success on the wire).
        var check = await _route.RouteExistsAsync(target_ip, localNet, timeout, ct);
        if (check.Exists)
            return new
            {
                success = true,
                action = "noop",
                reason = "route_already_exists",
                target_ip,
                target_net_id,
                local_net_id = _ads.LocalNetId,
                route_name = name,
            };

        if (dry_run)
            return new
            {
                success = false,
                action = "dry_run",
                reason = "route_status_unknown",
                hint = "Re-run without dry_run=true. AddRoute is idempotent — if the route already exists, the PLC will still answer success.",
                target_ip,
                target_net_id,
                local_net_id = _ads.LocalNetId,
                route_name = name,
            };

        // 3) Pull credentials. Saved → try them silently first. Otherwise pop
        //    the Windows credential dialog (same UI as RDP/SMB).
        string? user = null, pass = null;
        bool fromVault = false;
        if (!force_prompt && _cred.TryRead(target_ip, out var sUser, out var sPass))
        {
            user = sUser; pass = sPass; fromVault = true;
            _log.LogDebug("Using saved credentials for {Target}", target_ip);
        }

        // First attempt with saved creds (or, if none, prompt right away).
        if (user is null)
        {
            var ok = _cred.Prompt(target_ip,
                $"Enter PLC credentials to register route '{name}' on {target_ip}. The MCP needs to add an AMS backroute for our local NetId {_ads.LocalNetId}.",
                defaultUsername: "Administrator",
                out user, out pass, out var save);
            if (!ok)
                return new { success = false, error = "User cancelled the credential dialog." };
            if (save) TrySave(target_ip, user, pass);
        }

        var first = await _route.AddRouteAsync(target_ip, targetNet, localNet, localIp,
            name, user, pass, temporary, timeout, ct);
        ScrubString(ref pass);

        if (first.Success)
            return new
            {
                success = true,
                action = "added",
                target_ip,
                target_net_id,
                local_net_id = _ads.LocalNetId,
                route_name = name,
                temporary,
                used_saved_credentials = fromVault,
            };

        // 4) Auth failed with saved creds → drop them and re-prompt once.
        if (fromVault && first.Code == "auth_failed")
        {
            _cred.Delete(target_ip);
            _log.LogInformation("Saved credentials for {Target} rejected — removed and re-prompting", target_ip);
            var ok = _cred.Prompt(target_ip,
                $"Saved credentials for {target_ip} were rejected. Please enter new ones.",
                defaultUsername: user,
                out var u2, out var p2, out var save);
            if (!ok)
                return new { success = false, error = "Saved credentials rejected; user cancelled re-prompt." };

            var second = await _route.AddRouteAsync(target_ip, targetNet, localNet, localIp,
                name, u2, p2, temporary, timeout, ct);
            if (second.Success && save) TrySave(target_ip, u2, p2);
            ScrubString(ref p2);
            return new
            {
                success = second.Success,
                action = second.Success ? "added_after_reprompt" : "failed",
                error = second.Success ? null : second.Message,
                code  = second.Code,
                target_ip,
                target_net_id,
                local_net_id = _ads.LocalNetId,
                route_name = name,
            };
        }

        return new
        {
            success = false,
            action = "failed",
            error = first.Message,
            code = first.Code,
            target_ip,
            target_net_id,
            local_net_id = _ads.LocalNetId,
            route_name = name,
        };
    }

    private void TrySave(string ip, string user, string pass)
    {
        try { _cred.Save(ip, user, pass); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save credentials for {Ip}", ip); }
    }

    private static void ScrubString(ref string? s)
    {
        // Best-effort wipe — strings are immutable on the heap; we drop the
        // reference so the GC can collect it. The credential blob inside
        // CredUnPackAuthenticationBufferW is already zeroed before this.
        s = null;
    }

    /// <summary>Picks a local IPv4 on the same subnet as <paramref name="targetIp"/>, or any non-loopback IPv4 if no match.</summary>
    private static string? PickLocalIp(string targetIp)
    {
        if (!IPAddress.TryParse(targetIp, out var target)) return null;
        IPAddress? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                fallback ??= ua.Address;
                if (SameSubnet(ua.Address, target, ua.IPv4Mask))
                    return ua.Address.ToString();
            }
        }
        return fallback?.ToString();
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        if (ab.Length != bb.Length || ab.Length != mb.Length) return false;
        for (int i = 0; i < ab.Length; i++)
            if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
        return true;
    }
}
