using System.Runtime.InteropServices;
using BeckhoffMcp.AdsBridge.Bridge;
using BeckhoffMcp.AdsBridge.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.AdsBridge;

public static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? path);

    public static async Task Main(string[] args)
    {
        // Make Beckhoff TcAms* DLLs resolvable at runtime.
        var dllDir = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        if (!string.IsNullOrEmpty(dllDir) && Directory.Exists(dllDir))
        {
            SetDllDirectoryW(dllDir);
            Console.WriteLine($"DLL search path: {dllDir}");
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("ADSBRIDGE_")
            .AddCommandLine(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        var routesPath = builder.Configuration.GetValue<string>("StaticRoutesPath") ?? "StaticRoutes.xml";
        var routes = RouteTable.LoadFromXml(routesPath);

        builder.Services.AddSingleton(routes);
        builder.Services.AddSingleton<MqttForwarder>(sp =>
        {
            if (routes.Mqtt is null) throw new InvalidOperationException("No <Mqtt> block in StaticRoutes.xml");
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MqttForwarder>();
            return new MqttForwarder(routes.Mqtt, routes.LocalNetId, log);
        });
        builder.Services.AddSingleton<TcpForwarder>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<TcpForwarder>();
            return new TcpForwarder(log);
        });
        builder.Services.AddSingleton<LocalHandler>();
        builder.Services.AddSingleton<TcAmsServerHost>();
        builder.Services.AddHostedService<MqttBootstrapService>();
        builder.Services.AddHostedService<TcpListenerService>();
        builder.Services.AddHostedService<TcAmsServerBootstrap>();

        using var host = builder.Build();

        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdsBridge");
        log.LogInformation("Local: {Name} {NetId}", routes.LocalName, routes.LocalNetId);
        if (routes.Mqtt is { } m)
            log.LogInformation("MQTT broker: {Host}:{Port} topic={Topic}", m.Address, m.Port, m.Topic);
        foreach (var r in routes.Routes)
            log.LogInformation("Route: {Name} {NetId} via {Type} {Address}:{Port}", r.Name, r.NetId, r.Type, r.Address, r.Port);

        await host.RunAsync();
    }
}

/// <summary>Connects MqttForwarder before the TcpListener starts handling clients.</summary>
internal sealed class MqttBootstrapService : IHostedService
{
    private readonly MqttForwarder _mqtt;
    public MqttBootstrapService(MqttForwarder mqtt) => _mqtt = mqtt;
    public Task StartAsync(CancellationToken ct) => _mqtt.StartAsync(ct);
    public async Task StopAsync(CancellationToken ct) => await _mqtt.DisposeAsync();
}

/// <summary>Boots Beckhoff TcAmsServer.dll so TcAdsDll-based clients (pyads) find us via TcAmsWindow.</summary>
internal sealed class TcAmsServerBootstrap : IHostedService
{
    private readonly TcAmsServerHost _host;
    public TcAmsServerBootstrap(TcAmsServerHost host) => _host = host;
    public Task StartAsync(CancellationToken ct)
    {
        try { _host.Start(); }
        catch (Exception ex) { Console.Error.WriteLine($"TcAmsServer init failed: {ex.Message}"); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) { _host.Dispose(); return Task.CompletedTask; }
}
