using AstralProjection.Options;
using Cysharp.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Storage.Net.Blobs;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstralProjection
{
    public class ArcaneEyeWorker : BackgroundService
    {
        private readonly IServiceProvider provider;
        private readonly ArcaneEyeOptions options;
        private readonly ILogger logger;

        private readonly CrontabSchedule schedule;
        private DateTime nextRunDate;

        public ArcaneEyeWorker(IServiceProvider serviceProvider, ArcaneEyeOptions opts, ILogger<ArcaneEyeWorker> lgr)
        {
            provider = serviceProvider;
            options = opts;

            schedule = CrontabSchedule.Parse(opts.Schedule);
            nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
            logger = lgr;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run on startup.
            logger.LogInformation("Worker scheduled to generate module list on: {schedule}", options.Schedule);
            await ProcessAsync(stoppingToken);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Worker schedule triggered: {worker}", nameof(ArcaneEyeWorker));
                    await ProcessAsync(stoppingToken);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                    logger.LogInformation("Worker process completed: {worker}", nameof(ArcaneEyeWorker));
                    logger.LogInformation("Worker next execution should start at: {date}", nextRunDate);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            using var scope = provider.CreateScope();
            using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            await using var ms = new MemoryStream();

            try
            {
                await GenerateHtmlAsync(ms, storage, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to generate the list HTML");
            }

            try
            {
                await storage.WriteAsync(options.ListPath, ms, false, stoppingToken);
                logger.LogInformation("Uploaded the list HTML to remote: {path}", options.ListPath);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to upload HTML to remote: {path}", options.ListPath);
            }
        }

        private async Task GenerateHtmlAsync(Stream stream, IBlobStorage storage, CancellationToken stoppingToken = default)
        {
            using var sb = ZString.CreateUtf8StringBuilder();
            sb.Append(options.HtmlHeader);

            var manifests = await storage.ListAsync(new ListOptions
            {
                BrowseFilter = f => f.IsFile && f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
                Recurse = true
            }, stoppingToken);
            logger.LogInformation("Got manifest files in total: {count}", manifests.Count);

            foreach (var module in manifests.Where(m => m is not null && m.Name.Equals("module.json", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogTrace("Append module manifest link with: {path}", module.FullPath);
                sb.AppendFormat(options.LinkTemplate, options.Prefix, module.FullPath.TrimStart('/'));
            }

            // Horizontal rule?

            foreach (var system in manifests.Where(m => m is not null && m.Name.Equals("system.json", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogTrace("Append system manifest link with: {path}", system.FullPath);
                sb.AppendFormat(options.LinkTemplate, options.Prefix, system.FullPath.TrimStart('/'));
            }

            sb.Append(options.HtmlFooter);

            await sb.WriteToAsync(stream);
        }
    }
}
