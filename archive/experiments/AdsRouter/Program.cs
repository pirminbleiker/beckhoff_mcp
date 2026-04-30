using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;
using TwinCAT.Ads.SystemService;
using TwinCAT.Ads.TcpRouter;
using TwinCAT.Router;

namespace BeckhoffMcp.AdsRouter;

public static class Program
{
    public static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddStaticRoutesXmlConfiguration(null);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                cfg.AddEnvironmentVariables("ADS_");
                cfg.AddCommandLine(args);
            })
            .ConfigureServices(services => services.AddHostedService<RouterService>())
            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build()
            .Run();
    }
}

public sealed class RouterService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RouterService> _logger;

    public RouterService(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RouterService>();
    }

    protected override async Task ExecuteAsync(CancellationToken cancel)
    {
        // Required so MqttRouter can resolve AmsNetId.Local via static config
        GlobalConfiguration.Configuration = _configuration;

        var router = new AmsTcpIpRouter(_configuration, _loggerFactory);
        var adsRouterServer = new AdsRouterServer(router, _configuration, _loggerFactory);
        var systemService = new SystemServiceServer(router, _configuration, _loggerFactory);

        _logger.LogInformation("Starting embedded ADS router (TCP listener + MQTT plugin)");

        await Task.WhenAll(
            router.StartAsync(cancel),
            adsRouterServer.ConnectServerAndWaitAsync(cancel),
            systemService.ConnectServerAndWaitAsync(cancel)
        );
    }
}
