using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AstralProjection
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    // Inject config.
                    var opts = hostContext.Configuration.GetSection("Astral").Get<AstralOptions>();
                    services.AddSingleton(opts);

                    services.AddHostedService<AstralWorker>();
                });
    }
}
