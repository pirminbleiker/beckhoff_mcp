using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;

namespace BeckhoffMcp.AdsTestClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddCommandLine(args)
            .Build();

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConfiguration(config.GetSection("Logging"));
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        });
        var logger = loggerFactory.CreateLogger("AdsTestClient");

        // Required so the AdsOverMqtt plugin's MqttRouter resolves AmsNetId.Local
        GlobalConfiguration.Configuration = config;

        var targetNetIdStr = config.GetValue<string>("TargetNetId")
            ?? throw new InvalidOperationException("TargetNetId missing in config");
        var targetPort = config.GetValue<int?>("TargetPort") ?? 851;

        if (!AmsNetId.TryParse(targetNetIdStr, out var targetNetId))
        {
            logger.LogError("TargetNetId '{NetId}' not parseable", targetNetIdStr);
            return 1;
        }

        var target = new AmsAddress(targetNetId, targetPort);
        logger.LogInformation("Connecting to {Target} via AdsOverMqtt ...", target);

        try
        {
            using var session = new AdsSession(target, SessionSettings.Default, config, loggerFactory, null);
            var conn = (IAdsConnection)session.Connect();

            logger.LogInformation("Reading device information ...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var info = await conn.ReadDeviceInfoAsync(cts.Token);

            if (info.Succeeded)
            {
                logger.LogInformation("PLC says: Name='{Name}' Version={Version}",
                    info.DeviceInfo.Name, info.DeviceInfo.Version);
                logger.LogInformation("Reading ADS state ...");
                var state = await conn.ReadStateAsync(cts.Token);
                if (state.Succeeded)
                    logger.LogInformation("AdsState={Ads}, DeviceState={Dev}",
                        state.State.AdsState, state.State.DeviceState);
                return 0;
            }
            else
            {
                logger.LogError("ReadDeviceInfo failed: {Error}", info.ErrorCode);
                return 2;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed");
            return 3;
        }
    }
}
