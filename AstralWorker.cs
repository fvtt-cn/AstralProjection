using AstralProjection.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Newtonsoft.Json.Linq;
using Storage.Net.Blobs;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AstralProjection
{
    public class AstralWorker : BackgroundService
    {
        private readonly IServiceProvider provider;
        private readonly AstralOptions options;
        private readonly ILogger logger;

        private readonly CrontabSchedule schedule;
        private DateTime nextRunDate;

        public AstralWorker(IServiceProvider serviceProvider, AstralOptions opts, ILogger<AstralWorker> lgr)
        {
            provider = serviceProvider;
            options = opts;

            schedule = CrontabSchedule.Parse(opts.Schedule);
            nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
            logger = lgr;

            if (string.IsNullOrEmpty(options.Dir))
            {
                logger.LogError($"Configuration for {nameof(AstralWorker)} is invalid.");
                throw new ArgumentException(nameof(opts));
            }

            if (!Directory.Exists(options.Dir))
            {
                Directory.CreateDirectory(options.Dir);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run on startup.
            await ProcessAsync(stoppingToken).ConfigureAwait(false);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation($"{nameof(AstralWorker)} triggered.");
                    await ProcessAsync(stoppingToken).ConfigureAwait(false);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            using var httpClient = new HttpClient();
            using var scope = provider.CreateScope();
            using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            // All json.
            var dir = new DirectoryInfo(options.Dir);

            // Recursive.
            var files = dir.GetFiles("*.json", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation token requested, stopping...");
                    break;
                }

                try
                {
                    await ReadManifestAsync(storage, file, httpClient, stoppingToken);
                }
                catch (IOException ex)
                {
                    logger.LogError(ex, "Failed to handle {file}.", file.FullName);
                }
            }

            logger.LogInformation("Astral process completed.");
        }

        private async Task ReadManifestAsync(IBlobStorage storage,
            FileInfo file,
            HttpClient client,
            CancellationToken stoppingToken = default)
        {
            try
            {
                var jsonString = await File.ReadAllTextAsync(file.FullName, stoppingToken);
                var json = JObject.Parse(jsonString);

                var manifestUrl = json.Value<string>("manifest");
                var downloadUrl = json.Value<string>("download");

                if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(downloadUrl))
                {
                    logger.LogError("Manifest is invalid: {file}.", file.FullName);
                    return;
                }

                // Try to download new manifest url.
                var newManifestJson = await client.GetStringAsync(manifestUrl);
                var newManifest = JObject.Parse(newManifestJson);
                var newDownloadUrl = newManifest.Value<string>("download");

                // Truncate query string.
                var uploadManifestLoc = Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uploadManifestUri)
                    ? uploadManifestUri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path,
                        UriFormat.UriEscaped)
                    : null;
                var uploadDownloadLoc = string.Concat(uploadManifestLoc, ".zip");

                var onlineManifestUrl = string.Concat(options.Prefix, uploadManifestLoc);
                var onlineDownloadUrl = string.Concat(options.Prefix, uploadDownloadLoc);

                // It has updated.
                if (!JToken.DeepEquals(json, newManifest) && !string.IsNullOrEmpty(newDownloadUrl))
                {
                    // Replace local manifest with the new manifest.
                    await File.WriteAllTextAsync(file.FullName, newManifestJson, stoppingToken);
                }
                else if (!await storage.ExistsAsync(uploadManifestLoc, stoppingToken))
                {
                    logger.LogInformation("Manifest is not on the cloud, processing: {file}", file.FullName);
                }
                else
                {
                    // Skip.
                    // logger.LogInformation("Manifest is valid and is on the cloud: {file}", file.FullName);
                    return;
                }

                // Replace Json.
                newManifest["manifest"] = onlineManifestUrl;
                newManifest["download"] = onlineDownloadUrl;

                await UploadAsync(storage, newDownloadUrl, client, newManifest.ToString(), uploadManifestLoc,
                    uploadDownloadLoc);
                logger.LogInformation("Successfully updated: {file}", file.FullName);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to handle manifest '{file.FullName}'.", ex);
            }
        }

        private async Task UploadAsync(IBlobStorage storage,
            string downloadUrl,
            HttpClient client,
            string manifestJson,
            string uploadManifestLoc,
            string uploadDownloadLoc)
        {
            // Download zip.
            await using var zipStream = await DownloadAsync(downloadUrl, client, manifestJson);

            // Upload.
            if (zipStream != null && zipStream.CanRead)
            {
                await storage.WriteTextAsync(uploadManifestLoc, manifestJson);
                await storage.WriteAsync(uploadDownloadLoc, zipStream);
            }
        }

        private async Task<Stream> DownloadAsync(string downloadUrl, HttpClient client, string manifestJson)
        {
            await using var zipStream = await client.GetStreamAsync(downloadUrl);

            // Temp file stream.
            var tempFilePath = Path.GetTempFileName();
            var fileStream = File.Open(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            // Write new manifest.
            var tmpManifest = Path.GetTempFileName();
            await File.WriteAllTextAsync(tmpManifest, manifestJson);

            await zipStream.CopyToAsync(fileStream);

            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, true);

            foreach (var entry in zip.Entries)
            {
                // In the main folder or root.
                if (entry.FullName.Count(c => c == '/') <= 1 &&
                    (entry.Name.Equals("system.json") || entry.Name.Equals("module.json")))
                {
                    var manifestName = entry.FullName;
                    entry.Delete();

                    zip.CreateEntryFromFile(tmpManifest, manifestName);

                    return fileStream;
                }
            }

            logger.LogWarning($"Zip file '{downloadUrl}' manifest not found.");
            return null;
        }
    }
}
