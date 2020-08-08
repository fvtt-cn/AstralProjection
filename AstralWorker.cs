using COSSTS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Newtonsoft.Json.Linq;
using Storage.Net;
using Storage.Net.Blobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace AstralProjection
{
    public class AstralWorker : BackgroundService
    {
        private readonly CrontabSchedule schedule;
        private IBlobStorage storage;
        private readonly AstralOptions options;

        private readonly ILogger<AstralWorker> logger;

        private DateTime nextRunDate;

        // 12 hours to refresh.
        private const string SCHEDULE = "30 */12 * * *";
        private const int CHECK_DELAY_MS = 5000;

        /// <exception cref="SecurityException">Ignore.</exception>
        public AstralWorker(AstralOptions opts, ILogger<AstralWorker> lgr)
        {
            options = opts;

            schedule = CrontabSchedule.Parse(SCHEDULE);
            nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
            logger = lgr;
        }

        private void ConnectStorage()
        {
            // Dispose at first.
            storage?.Dispose();

            // QCloud Specific.
            var allowActions = new string[] { "*" };
            var dict = new Dictionary<string, object>
            {
                { "bucket", options.Bucket },
                { "region", options.Region },
                { "allowActions", allowActions },
                { "allowPrefix", "*" },
                { "durationSeconds", 7200 },
                { "secretId", options.Id },
                { "secretKey", options.Key }
            };
            var result = STSClient.genCredential(dict);
            var c = result["Credentials"] as JToken;

            // S3.
            storage = StorageFactory.Blobs.AwsS3(
                c.Value<string>("TmpSecretId"),
                c.Value<string>("TmpSecretKey"),
                c.Value<string>("Token"),
                options.Bucket,
                null,
                options.ServiceUrl);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ConnectStorage();
            await ProcessAsync(stoppingToken);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Crontab triggered.");

                    ConnectStorage();
                    await ProcessAsync(stoppingToken);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                }

                // Wait 5s to check.
                await Task.Delay(CHECK_DELAY_MS);
            }
            while (!stoppingToken.IsCancellationRequested);
        }

        /// <exception cref="SecurityException">Ignore.</exception>
        /// <exception cref="PathTooLongException">Ignore.</exception>
        /// <exception cref="DirectoryNotFoundException">Ignore.</exception>
        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            if (!string.IsNullOrEmpty(options.Dir) && Directory.Exists(options.Dir))
            {
                // All jsons.
                var dir = new DirectoryInfo(options.Dir);

                // Recursive.
                var files = dir.GetFiles("*.json", SearchOption.AllDirectories);
                using var httpClient = new HttpClient();

                foreach (var file in files)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Cancellation token requested, stopping...");
                        break;
                    }

                    try
                    {
                        await ReadManifestAsync(file, httpClient, stoppingToken);
                    }
                    catch (IOException ex)
                    {
                        logger.LogError(ex, "File path: {file}", file.FullName);
                    }
                }

                logger.LogInformation("Process completed.");
            }
        }

        /// <exception cref="IOException">Ignore.</exception>
        private async Task ReadManifestAsync(FileInfo file, HttpClient client, CancellationToken stoppingToken = default)
        {
            try
            {
                var jsonString = await File.ReadAllTextAsync(file.FullName, stoppingToken);
                var json = JObject.Parse(jsonString);

                var manifestUrl = json.Value<string>("manifest");
                var downloadUrl = json.Value<string>("download");

                if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(downloadUrl))
                {
                    logger.LogError("Manifest is invalid: {file}", file.FullName);
                    return;
                }

                // Try to download new manifest url.
                var newManifestJson = await client.GetStringAsync(manifestUrl);
                var newManifest = JObject.Parse(newManifestJson);
                var newDownloadUrl = newManifest.Value<string>("download");

                // Truncate query string.
                var uploadManifestLoc = Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uploadManifestUri)
                    ? uploadManifestUri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped)
                    : null;
                var uploadDownloadLoc = string.Concat(uploadManifestLoc, ".zip");

                var onlineManifestUrl = string.Concat(options.Prefix, uploadManifestLoc);
                var onlineDownloadUrl = string.Concat(options.Prefix, uploadDownloadLoc);

                // Replace Json.
                newManifest["manifest"] = onlineManifestUrl;
                newManifest["download"] = onlineDownloadUrl;

                // It has updated.
                if (!JToken.DeepEquals(json, newManifest) && !string.IsNullOrEmpty(newDownloadUrl))
                {
                    // Replace local manifest with the new manifest.
                    await File.WriteAllTextAsync(file.FullName, newManifestJson);
                }
                else if (!await storage.ExistsAsync(uploadManifestLoc))
                {
                    logger.LogInformation("Manifest is not on the cloud, processing: {file}", file.FullName);
                }
                else
                {
                    // Skip.
                    logger.LogInformation("Manifest is valid and is on the cloud: {file}", file.FullName);
                    return;
                }

                await UploadAsync(newDownloadUrl, client, newManifest.ToString(), uploadManifestLoc, uploadDownloadLoc);
                logger.LogInformation("Uploaded for the update: {file}", file.FullName);
            }
            catch (Exception ex)
            {
                throw new IOException("Reading manifest failed.", ex);
            }
        }

        private async Task UploadAsync(string downloadUrl, HttpClient client, string manifestJson, string uploadManifestLoc, string uploadDownloadLoc)
        {
            // Download zip.
            using var zipStream = await DownloadAsync(downloadUrl, client, manifestJson);

            // Upload.
            if (zipStream != null && zipStream.CanRead)
            {
                await storage.WriteTextAsync(uploadManifestLoc, manifestJson);
                await storage.WriteAsync(uploadDownloadLoc, zipStream);
            }
        }

        /// <exception cref="HttpRequestException">Ignore.</exception>
        /// <exception cref="InvalidDataException">Ignore.</exception>
        /// <exception cref="SecurityException">Ignore.</exception>
        /// <exception cref="IOException">Ignore.</exception>
        /// <exception cref="UnauthorizedAccessException">Ignore.</exception>
        /// <exception cref="PathTooLongException">Ignore.</exception>
        /// <exception cref="DirectoryNotFoundException">Ignore.</exception>
        /// <exception cref="ObjectDisposedException">Ignore.</exception>
        /// <exception cref="ObjectDisposedException">Ignore.</exception>
        private async Task<Stream> DownloadAsync(string downloadUrl, HttpClient client, string manifestJson)
        {
            using var zipStream = await client.GetStreamAsync(downloadUrl);

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
                if (entry.FullName.Count(c => c == '/') <= 1 && (entry.Name.Equals("system.json") || entry.Name.Equals("module.json")))
                {
                    var manifestName = entry.FullName;
                    entry.Delete();

                    zip.CreateEntryFromFile(tmpManifest, manifestName);

                    return fileStream;
                }
            }

            logger.LogWarning("Zip file manifest not found.");
            return null;
        }

        public override void Dispose()
        {
            storage?.Dispose();
            base.Dispose();
        }
    }
}
