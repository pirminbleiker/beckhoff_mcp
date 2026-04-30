using BeckhoffMcp.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeckhoffMcp.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables("BECKHOFF_MCP_")
            .AddCommandLine(args);

        // MCP uses stdio for transport — keep stderr clean by routing logs there
        // Logging strictly to stderr — stdout is reserved for MCP JSON-RPC traffic.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        builder.Services.AddSingleton<AdsConnectionManager>();
        builder.Services.AddSingleton<NetworkDiscovery>();
        builder.Services.AddSingleton<TraceService>();
        builder.Services.AddSingleton<RouteRegistration>();
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<WindowsCredentialPrompt>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        using var host = builder.Build();
        await host.RunAsync();
    }
}
