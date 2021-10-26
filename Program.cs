using AstralProjection.Options;
using AstralProjection.Services;
using Cysharp.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace AstralProjection
{
    public sealed class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddZLoggerConsole(options =>
                    {
                        var prefixFormat = ZString.PrepareUtf8<string, string, string>("{0} | [{1}] <{2}> ");
                        options.PrefixFormatter = (writer, info) => prefixFormat.FormatTo(ref writer,
                            info.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), ColorizeLogLevel(info.LogLevel),
                            info.CategoryName);
                    });
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Inject worker options directly instead of IOptions.
                    var astral = hostContext.Configuration.GetSection("Astral").Get<AstralOptions>();
                    if (astral is not null)
                    {
                        services.AddSingleton(astral);
                        services.AddHostedService<AstralWorker>();
                    }

                    var forge = hostContext.Configuration.GetSection("Forge").Get<ForgeOptions>();
                    if (forge is not null)
                    {
                        services.AddSingleton(forge);
                        services.AddHostedService<ForgeWorker>();
                    }

                    var arcaneEye = hostContext.Configuration.GetSection("ArcaneEye").Get<ArcaneEyeOptions>();
                    if (arcaneEye is not null)
                    {
                        services.AddSingleton(arcaneEye);
                        services.AddHostedService<ArcaneEyeWorker>();
                    }

                    // Inject dependency.
                    services.AddQCloudStorage(s3 => hostContext.Configuration.GetSection("S3").Bind(s3));
                });

        private static string ColorizeLogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "\x1b[30mtrce\x1b[0m",
            LogLevel.Debug => "\x1b[34mdebg\x1b[0m",
            LogLevel.Information => "\x1b[32minfo\x1b[0m",
            LogLevel.Warning => "\x1b[33mwarn\x1b[0m",
            LogLevel.Error => "\x1b[31mfail\x1b[0m",
            LogLevel.Critical => "\x1b[91mcrit\x1b[0m",
            LogLevel.None or _ => "\x1b[37mnone\x1b[0m"
        };
    }
}