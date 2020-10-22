using AstralProjection.Options;
using AstralProjection.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
                .ConfigureServices((hostContext, services) =>
                {
                    // Inject worker options directly instead of IOptions.
                    var astral = hostContext.Configuration.GetSection("Astral").Get<AstralOptions>();
                    services.AddSingleton(astral);
                    var forge = hostContext.Configuration.GetSection("Forge").Get<ForgeOptions>();
                    services.AddSingleton(forge);

                    // Inject dependency.
                    services.AddQCloudStorage(s3 => hostContext.Configuration.GetSection("S3").Bind(s3));

                    // Add workers.
                    services.AddHostedService<AstralWorker>();
                    services.AddHostedService<ForgeWorker>();
                });
    }
}
