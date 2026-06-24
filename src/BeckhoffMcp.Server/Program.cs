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
        // Last-resort diagnostics: a fault on a background thread/task (e.g. the
        // in-process AmsTcpIpRouter receive loop choking on a truncated AMS
        // frame over a lossy link) can tear down the whole process, which the
        // MCP client only sees as an opaque "-32000 Connection closed". Surface
        // the real stack trace on stderr (stdout is reserved for JSON-RPC) so
        // such crashes are diagnosable instead of silent. SetObserved keeps an
        // unobserved task exception from escalating to a process kill.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[FATAL] UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        // Anchor the content root to the directory the exe lives in. Without
        // this the host uses Directory.GetCurrentDirectory(), which means a
        // persistent appsettings.json is missed when Claude Desktop launches
        // the MCP from a different cwd (e.g. %USERPROFILE%).
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = exeDir,
            Args = args,
        });

        // Config layering, lowest precedence first:
        //   1) Embedded default appsettings.json baked into the exe — gives
        //      the MCP a working config out of the box, no extra file needed.
        //   2) Optional appsettings.json next to the exe (overrides). The
        //      AdsConnectionManager writes a generated NetId here on first
        //      launch so the same identity persists across runs.
        //   3) BECKHOFF_MCP_APPSETTINGS env var pointing at any other file.
        //   4) BECKHOFF_MCP_* env vars.
        //   5) Command line args (--Beckhoff:TargetNetId=...).
        using var defaultsStream = typeof(Program).Assembly
            .GetManifestResourceStream("BeckhoffMcp.Server.appsettings.json")
            ?? throw new InvalidOperationException("Embedded default appsettings.json missing.");
        builder.Configuration.AddJsonStream(defaultsStream);

        var diskConfig = Path.Combine(exeDir, "appsettings.json");
        builder.Configuration.AddJsonFile(diskConfig, optional: true, reloadOnChange: false);

        var envConfig = Environment.GetEnvironmentVariable("BECKHOFF_MCP_APPSETTINGS");
        if (!string.IsNullOrEmpty(envConfig))
            builder.Configuration.AddJsonFile(envConfig, optional: false, reloadOnChange: false);

        builder.Configuration
            .AddEnvironmentVariables("BECKHOFF_MCP_")
            .AddCommandLine(args);

        // MCP uses stdio for transport — keep stderr clean by routing logs there
        // Logging strictly to stderr — stdout is reserved for MCP JSON-RPC traffic.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        builder.Services.AddSingleton<LocalRouterDetector>();
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
