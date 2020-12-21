using AstralProjection.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Storage.Net.Blobs;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
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

            ConfigureJsonDirectory();
        }

        private void ConfigureJsonDirectory()
        {
            // Configure.
            if (string.IsNullOrEmpty(options.Dir))
            {
                logger.LogError("Configuration is invalid for: {worker}", nameof(AstralWorker));
                throw new ArgumentException("Directory name is null or empty", nameof(options));
            }

            Directory.CreateDirectory(options.Dir);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run on startup.
            await ProcessAsync(stoppingToken);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Worker schedule triggered: {worker}", nameof(AstralWorker));
                    await ProcessAsync(stoppingToken);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            // Initialize HttpClient and BlobStorage when processing.
            using var httpClient = new HttpClient();
            using var scope = provider.CreateScope();
            using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            // Search all json recursively.
            var dir = new DirectoryInfo(options.Dir);
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
                    await ProcessFileAsync(file, httpClient, storage, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process: {file}", file.FullName);
                }
            }

            logger.LogInformation("Worker process completed: {worker}", nameof(AstralWorker));
        }

        private async Task ProcessFileAsync(FileInfo file, HttpClient httpClient, IBlobStorage storage, CancellationToken stoppingToken = default)
        {
            var (localJson, localMfUrl, _) = await ReadLocalManifestAsync(file, stoppingToken);
            var (remoteJson, remoteMfUrl, remoteDlUrl) = await ReadRemoteManifestAsync(localMfUrl, httpClient, stoppingToken);
            var title = remoteJson.Value<string>("title");

            // Truncate protocol and query string.
            var (astralMfName, astralDlName) = GenerateAstralFullName(remoteMfUrl);

            var astralMfUrl = string.Concat(options.Prefix, astralMfName);
            var astralDlUrl = string.Concat(options.Prefix, astralDlName);

            // Replace local manifest with the new manifest if updated.
            var manifestUpdated = await UpdateLocalManifestAsync(file, localJson, remoteJson);
            var alreadyExists = await storage.ExistsAsync(astralMfName, stoppingToken);

            if (manifestUpdated || !alreadyExists)
            {
                // If the manifest changed/updated or does not exist in the folder, upload it to the cloud.
                remoteJson["manifest"] = astralMfUrl;
                remoteJson["download"] = astralDlUrl;
                var astralJsonString = remoteJson.ToString();

                var manifestType = astralMfName.EndsWith("system.json", StringComparison.OrdinalIgnoreCase) ? "system" : "module";
                await using var zipStream = await DownloadZipAsync(remoteDlUrl, astralJsonString, manifestType, httpClient, stoppingToken);

                // Upload with timeout set (linked to the stoppingToken).
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(options.UploadTimeout * 1000);

                try
                {
                    await storage.WriteAsync(astralDlName, zipStream, false, timeoutCts.Token);
                    await storage.WriteTextAsync(astralMfName, astralJsonString, Encoding.UTF8, timeoutCts.Token);

                    logger.LogInformation("Updated the {type} named {title} from: {file}", manifestType, title, file.FullName);
                }
                catch (OperationCanceledException oce)
                {
                    logger.LogError(oce, "Failed to upload files to the storage due to timeout for: {file}", file.FullName);
                }
            }
        }

        private async Task<(JObject Json, string ManifestUrl, string DownloadUrl)> ReadLocalManifestAsync(FileInfo file,
            CancellationToken stoppingToken = default)
        {
            try
            {
                var jsonString = await File.ReadAllTextAsync(file.FullName, stoppingToken);
                return ReadManifest(jsonString);
            }
            catch (IOException ioe)
            {
                logger.LogError(ioe, "Unable to read contents due to IO errors from: {file}", file.FullName);
                throw;
            }
            catch (UnauthorizedAccessException uae)
            {
                logger.LogError(uae, "Unable to read contents due to unauthorized access from: {file}", file.FullName);
                throw;
            }
            catch (SecurityException se)
            {
                logger.LogError(se, "Unable to read contents due to file security limit from: {file}", file.FullName);
                throw;
            }
            catch (JsonReaderException jre)
            {
                logger.LogError(jre, "Json read invalid string from: {file}", file.FullName);
                throw;
            }
            catch (ArgumentException ae)
            {
                logger.LogError(ae, "Necessary properties not in: {file}", file.FullName);
                throw;
            }
            catch (OperationCanceledException oce)
            {
                logger.LogError(oce, "Manifest reading operation cancelled for: {file}", file.FullName);
                throw;
            }
        }

        private async Task<(JObject Json, string ManifestUrl, string DownloadUrl)> ReadRemoteManifestAsync(
            string manifestUrl,
            HttpClient client,
            CancellationToken stoppingToken = default)
        {
            try
            {
                var jsonString = await client.GetStringAsync(manifestUrl, stoppingToken);
                return ReadManifest(jsonString);
            }
            catch (HttpRequestException hre)
            {
                logger.LogError(hre, "HTTP error occurred when GET manifest string from: {url}", manifestUrl);
                throw;
            }
            catch (JsonReaderException jre)
            {
                logger.LogError(jre, "Json read invalid string from: {url}", manifestUrl);
                throw;
            }
            catch (ArgumentException ae)
            {
                logger.LogError(ae, "Necessary properties not in: {url}", manifestUrl);
                throw;
            }
            catch (OperationCanceledException oce)
            {
                logger.LogError(oce, "Manifest reading operation cancelled for: {url}", manifestUrl);
                throw;
            }
        }

        private (string AstralManifestName, string AstralDownloadName) GenerateAstralFullName(string manifestUrl)
        {
            if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var astralMfUri))
            {
                var astralMfName = astralMfUri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped);
                var astralDlName = string.Concat(astralMfName, ".zip");

                return (astralMfName, astralDlName);
            }

            logger.LogError("Unable to transform the manifest URL into Uri for: {url}", manifestUrl);
            throw new ArgumentException("URL is invalid", nameof(manifestUrl));
        }

        private async Task<bool> UpdateLocalManifestAsync(FileInfo file, JObject local, JObject remote)
        {
            if (JToken.DeepEquals(local, remote))
            {
                return false;
            }

            logger.LogInformation("Local manifest updated for: {file}", file.FullName);
            await File.WriteAllTextAsync(file.FullName, remote.ToString());
            return true;
        }

        private async Task<Stream> DownloadZipAsync(string downloadUrl,
            string astralJsonString,
            string manifestType,
            HttpClient client,
            CancellationToken stoppingToken = default)
        {
            // Temp zip stream for later using, do not dispose.
            var tmpZipName = Path.GetTempFileName();
            var zipStream = File.Open(tmpZipName, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            // Get stream and download to the temp file.
            await using var dlStream = await client.GetStreamAsync(downloadUrl, stoppingToken);
            await dlStream.CopyToAsync(zipStream, stoppingToken);

            // Open the zip file.
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Update, true);
            var replaced = false;
            var manifestName = string.Concat(manifestType, ".json");

            foreach (var entry in zip.Entries.Where(e => e.Name.Equals(manifestName)))
            {
                await using var entryStream = entry.Open();
                entryStream.SetLength(0);

                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync(astralJsonString);

                replaced = true;
            }

            if (!replaced)
            {
                logger.LogWarning("Manifest file not found in the zip file for: {url}", downloadUrl);
                await zipStream.DisposeAsync();
            }

            if (!zipStream.CanRead)
            {
                logger.LogError("Failed to download the zip from: {url}", downloadUrl);
                throw new ArgumentException("Download URL is invalid", downloadUrl);
            }

            logger.LogTrace("Zip file is updated and ready to be uploaded for: {url}", downloadUrl);
            return zipStream;
        }

        private (JObject Json, string ManifestUrl, string DownloadUrl) ReadManifest(string jsonString)
        {
            var json = JObject.Parse(jsonString);

            var manifestUrl = json.Value<string>("manifest");
            var downloadUrl = json.Value<string>("download");

            if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(downloadUrl))
            {
                logger.LogWarning("Manifest does not include manifest/download URLs: {jsonString}", jsonString);
                throw new ArgumentException("Json is invalid", nameof(jsonString));
            }

            logger.LogTrace("Extract URLs for the manifest: {manifest}, {download}", manifestUrl, downloadUrl);
            return (json, manifestUrl, downloadUrl);
        }
    }
}
