using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstralProjection.Options;
using Cysharp.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Storage.Net.Blobs;

namespace AstralProjection
{
    public class AstralWorker : BackgroundService
    {
        private readonly ILogger logger;
        private readonly AstralOptions options;
        private readonly IServiceProvider provider;

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
                logger.LogCritical("Configuration is invalid for: {worker}", nameof(AstralWorker));
                throw new ArgumentException("Directory name is null or empty", nameof(options));
            }

            Directory.CreateDirectory(options.Dir);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run on startup.
            logger.LogInformation("Worker scheduled to refresh module/system in: {dir} on {schedule}", options.Dir,
                options.Schedule);
            await ProcessAsync(stoppingToken);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Worker schedule triggered: {worker}", nameof(AstralWorker));
                    await ProcessAsync(stoppingToken);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                    logger.LogInformation("Worker process completed: {worker}", nameof(AstralWorker));
                    logger.LogInformation("Worker next execution should start at: {date}", nextRunDate);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            // Search all json recursively.
            var dir = new DirectoryInfo(options.Dir);
            var files = dir.GetFiles("*.json", SearchOption.AllDirectories);
            logger.LogInformation("Got manifest files in total: {count}", files.Length);

            // Initialize HttpClient and BlobStorage when processing.
            using var httpClient = new HttpClient();

            // Sequential because the most time-consuming part is network I/O.
            foreach (var file in files)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation token requested, stopping...");
                    return;
                }

                try
                {
                    // Create IBlobStorage.
                    using var scope = provider.CreateScope();
                    using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

                    await ProcessFileAsync(file, httpClient, storage, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process: {file}", file.FullName);
                }
            }
        }

        private async Task ProcessFileAsync(FileInfo file, HttpClient httpClient, IBlobStorage storage,
            CancellationToken stoppingToken = default)
        {
            var (localJson, localMfUrl, _) = await ReadLocalManifestAsync(file, stoppingToken);
            var (remoteJson, remoteMfUrl, remoteDlUrl) =
                await ReadRemoteManifestAsync(localMfUrl, httpClient, stoppingToken);
            var title = remoteJson.Value<string>("title");

            // Truncate protocol and query string.
            var (astralMfName, astralDlName) = GenerateAstralFullName(remoteMfUrl);

            var astralMfUrl = ZString.Concat(options.Prefix, astralMfName);
            var astralDlUrl = ZString.Concat(options.Prefix, astralDlName);

            // Replace local manifest with the new manifest if updated.
            var manifestToUpdate = !JToken.DeepEquals(localJson, remoteJson);
            var alreadyExists = await storage.ExistsAsync(astralMfName, stoppingToken);
            var astralJson = remoteJson.DeepClone();

            if (!manifestToUpdate && alreadyExists)
            {
                logger.LogTrace("Manifest is up to date and files already exist on the cloud: {title} from {file}",
                    title, file.FullName);
                return;
            }

            // If the manifest changed/updated or does not exist in the folder, upload it to the cloud.
            astralJson["manifest"] = astralMfUrl;
            astralJson["download"] = astralDlUrl;
            var astralJsonString = astralJson.ToString();
            var moduleName = astralJson.Value<string>("name");

            var manifestType = astralMfName.EndsWith("system.json", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "module";
            await using var zipStream = await DownloadZipAsync(remoteDlUrl, astralJsonString, manifestType, moduleName,
                httpClient, stoppingToken);

            // Upload with timeout set (linked to the stoppingToken).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(options.UploadTimeout * 1000);

            try
            {
                await storage.WriteAsync(astralDlName, zipStream, false, timeoutCts.Token);
                await storage.WriteTextAsync(astralMfName, astralJsonString, Encoding.UTF8, timeoutCts.Token);
                // Update local manifest only on storage updated.
                if (manifestToUpdate)
                {
                    await UpdateLocalManifestAsync(file, remoteJson);
                }

                logger.LogInformation("Updated the {type} named {title} from: {file}", manifestType, title,
                    file.FullName);
            }
            catch (OperationCanceledException oce)
            {
                logger.LogError(oce, "Failed to upload files to the storage due to timeout for: {file}", file.FullName);
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
            string manifestUrl, HttpClient client, CancellationToken stoppingToken = default)
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
                // Unescape the stupid AF `%2F` between org and repo from the GitLab API. 
                var astralMfName =
                    astralMfUri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path,
                        UriFormat.Unescaped);
                var astralDlName = ZString.Concat(astralMfName, ".zip");

                return (astralMfName, astralDlName);
            }

            logger.LogError("Unable to transform the manifest URL into Uri for: {url}", manifestUrl);
            throw new ArgumentException("URL is invalid", nameof(manifestUrl));
        }

        private async Task UpdateLocalManifestAsync(FileInfo file, JObject remote)
        {
            logger.LogInformation("Local manifest updated for: {file}", file.FullName);
            await File.WriteAllTextAsync(file.FullName, remote.ToString());
        }

        private async Task<Stream> DownloadZipAsync(string downloadUrl, string astralJsonString, string manifestType,
            string moduleName, HttpClient client, CancellationToken stoppingToken = default)
        {
            // Temp zip stream for later using, do not dispose.
            var tmpZipName = Path.GetTempFileName();
            var zipStream = File.Open(tmpZipName, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            // Get stream and download to the temp file.
            await using var dlStream = await client.GetStreamAsync(downloadUrl, stoppingToken);
            await dlStream.CopyToAsync(zipStream, stoppingToken);

            // Validate the size. IOException if it cannot fetch the file size.
            if (zipStream.Length > options.SizeLimit)
            {
                logger.LogError("Zip file is downloaded but its size is too big (in bytes): {size} > {limit}",
                    zipStream.Length, options.SizeLimit);
                throw new ArgumentOutOfRangeException(nameof(zipStream.Length), "Download URL is invalid");
            }

            // Open the zip file.
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Update, true);
            var replaced = false;
            var manifestName = ZString.Concat(manifestType, ".json");

            foreach (var entry in zip.Entries.Where(e => e.Name.Equals(manifestName)))
            {
                await using var entryStream = entry.Open();
                string entryName;

                try
                {
                    using var textReader = new StreamReader(entryStream, leaveOpen: true);
                    using var jsonReader = new JsonTextReader(textReader);
                    var entryJson = await JObject.LoadAsync(jsonReader, stoppingToken);
                    entryName = entryJson.Value<string>("name");
                }
                catch (JsonReaderException jre)
                {
                    logger.LogWarning(jre, "Manifest file found in zip but unable to convert it: {entry} of {module}",
                        entry.FullName, moduleName);
                    continue;
                }

                if (entryName == null || !moduleName.Equals(entryName, StringComparison.Ordinal))
                {
                    logger.LogTrace("Manifest file found in zip but its name is not equal: {name} of {path}", entryName,
                        entry.FullName);
                    continue;
                }

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
                throw new ArgumentException("Download URL is invalid", nameof(downloadUrl));
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