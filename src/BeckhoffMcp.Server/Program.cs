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
        // Anchor the content root to the directory the exe lives in. Without
        // this the host uses Directory.GetCurrentDirectory(), which means
        // appsettings.json is missed when Claude Desktop launches the MCP
        // from a different cwd (e.g. %USERPROFILE%).
        var exeDir = AppContext.BaseDirectory;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = exeDir,
            Args = args,
        });

        // Load appsettings.json explicitly from the exe directory so it works
        // regardless of cwd. BECKHOFF_MCP_APPSETTINGS env var lets the user
        // override the path entirely.
        var configPath = Environment.GetEnvironmentVariable("BECKHOFF_MCP_APPSETTINGS")
                         ?? Path.Combine(exeDir, "appsettings.json");
        builder.Configuration
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
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
