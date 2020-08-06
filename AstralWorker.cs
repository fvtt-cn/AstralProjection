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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AstralProjection
{
    public class AstralWorker : BackgroundService
    {
        private readonly CrontabSchedule schedule;
        private DateTime nextRunDate;
        private readonly ILogger<AstralWorker> logger;

        private readonly IBlobStorage storage;

        private readonly AstralOptions options;

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

            // Initialize.
            var allowActions = new string[] { "*" };
            var dict = new Dictionary<string, object>
            {
                { "bucket", options.Bucket },
                { "region", options.Region },
                { "allowActions", allowActions },
                { "allowPrefix", "*" },
                { "durationSeconds", 3600 },
                { "secretId", options.Id },
                { "secretKey", options.Key }
            };

            var result = STSClient.genCredential(dict);
            var c = result["Credentials"] as JToken;

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
            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Crontab triggered.");

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
                var dir = new DirectoryInfo(options.Dir);

                // Recursive.
                var files = dir.GetFiles("*.json", SearchOption.AllDirectories);
                using var httpClient = new HttpClient();

                foreach (var file in files)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await ReadManifestAsync(file, httpClient, stoppingToken);
                    }
                    catch (OperationCanceledException ex)
                    {
                        logger.LogError(ex, "File path: {file}", file.FullName);
                    }
                }
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

                if (!string.IsNullOrEmpty(manifestUrl) && !string.IsNullOrEmpty(downloadUrl))
                {
                    // Try to download new manifest url.
                    var newManifestJson = await client.GetStringAsync(manifestUrl);
                    var newManifest = JObject.Parse(newManifestJson);

                    var regex = new Regex("^https://|^http://");
                    var onlineManifestUrl = regex.Replace(manifestUrl, options.Prefix);
                    var onlineDownloadUrl = string.Concat(onlineManifestUrl, ".zip");
                    var uploadManifestLoc = regex.Replace(manifestUrl, "");
                    var uploadDownloadLoc = string.Concat(uploadManifestLoc, ".zip");

                    if (!JToken.DeepEquals(json, newManifest))
                    {
                        var newDownloadUrl = newManifest.Value<string>("download");

                        if (!string.IsNullOrEmpty(newDownloadUrl))
                        {
                            // Replace local manifest with the new manifest.
                            await File.WriteAllTextAsync(file.FullName, newManifestJson);

                            // Replace Json.
                            newManifest["manifest"] = onlineManifestUrl;
                            newManifest["download"] = onlineDownloadUrl;

                            // Download zip.
                            using var zipStream = await DownloadAsync(newDownloadUrl, client, newManifest);

                            // Upload.
                            await storage.WriteTextAsync(uploadManifestLoc, newManifest.ToString());
                            await storage.WriteAsync(uploadDownloadLoc, zipStream);

                            logger.LogInformation("Uploaded for the update: {file}", file.FullName);
                        }
                    }
                    else
                    {
                        logger.LogInformation("Manifest is valid but there is no update: {file}", file.FullName);

                        // Check if there is.
                        if (!await storage.ExistsAsync(uploadManifestLoc))
                        {
                            logger.LogInformation("Manifest is not on the cloud, processing: {file}", file.FullName);

                            // Replace Json.
                            newManifest["manifest"] = onlineManifestUrl;
                            newManifest["download"] = onlineDownloadUrl;

                            // Download zip.
                            using var zipStream = await DownloadAsync(downloadUrl, client, newManifest);

                            // Upload.
                            await storage.WriteTextAsync(uploadManifestLoc, newManifest.ToString());
                            await storage.WriteAsync(uploadDownloadLoc, zipStream);

                            logger.LogInformation("Uploaded for the first time: {file}", file.FullName);
                        }
                    }
                }
                else
                {
                    logger.LogError("Manifest is invalid: {file}", file.FullName);
                }
            }
            catch (Exception ex)
            {
                throw new IOException("Reading manifest failed.", ex);
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
        private async Task<Stream> DownloadAsync(string downloadUrl, HttpClient client, JObject newManifest, CancellationToken stoppingToken = default)
        {
            using var zipStream = await client.GetStreamAsync(downloadUrl);

            // Temp file stream.
            var tempFilePath = Path.GetTempFileName();
            var fileStream = File.Open(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            // Write new manifest.
            var tmpManifest = Path.GetTempFileName();
            await File.WriteAllTextAsync(tmpManifest, newManifest.ToString());

            await zipStream.CopyToAsync(fileStream);

            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, true);

            foreach (var entry in zip.Entries)
            {
                // In the main folder.
                if (entry.FullName.Count(c => c == '/') == 1 && (entry.Name.Equals("system.json") || entry.Name.Equals("module.json")))
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
