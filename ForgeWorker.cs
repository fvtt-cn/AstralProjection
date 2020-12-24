using AngleSharp;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AstralProjection.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using Storage.Net;
using Storage.Net.Blobs;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

namespace AstralProjection
{
    public class ForgeWorker : BackgroundService
    {
        private readonly IServiceProvider provider;
        private readonly ForgeOptions options;
        private readonly ILogger logger;

        private readonly CrontabSchedule schedule;
        private DateTime nextRunDate;

        private string[] platforms;

        public ForgeWorker(IServiceProvider serviceProvider, ForgeOptions opts, ILogger<ForgeWorker> lgr)
        {
            provider = serviceProvider;
            options = opts;

            schedule = CrontabSchedule.Parse(opts.Schedule);
            nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
            logger = lgr;

            ConfigurePlatforms();
        }

        private void ConfigurePlatforms()
        {
            // Default all platforms.
            platforms = options.Platforms ?? GlobalSettings.SUPPORTED_PLATFORMS;
            platforms = platforms.Where(x => GlobalSettings.SUPPORTED_PLATFORMS.Contains(x)).ToArray();

            if (string.IsNullOrEmpty(options.Username) || string.IsNullOrEmpty(options.Password) || !options.Minimum.IsVersion() || !platforms.Any())
            {
                logger.LogCritical("Configuration is invalid for: {worker}", nameof(ForgeWorker));
                throw new ArgumentException("Platform or version is invalid", nameof(options));
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run on startup.
            logger.LogInformation("Worker scheduled to refresh from {version} for platforms: {plats} on {schedule}", options.Minimum,
                string.Join(", ", platforms), options.Schedule);
            await ProcessAsync(stoppingToken)
                .ContinueWith(t => logger.LogError(t.Exception, "Execution interrupted"), TaskContinuationOptions.OnlyOnFaulted);

            do
            {
                if (DateTime.Now > nextRunDate)
                {
                    logger.LogInformation("Worker schedule triggered: {worker}", nameof(ForgeWorker));
                    await ProcessAsync(stoppingToken)
                        .ContinueWith(t => logger.LogError(t.Exception, "Execution interrupted"), TaskContinuationOptions.OnlyOnFaulted);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                    logger.LogInformation("Worker process completed: {worker}", nameof(ForgeWorker));
                    logger.LogInformation("Worker next execution should start at: {date}", nextRunDate);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            using var context = BrowsingContext.New(Configuration.Default.WithDefaultCookies().WithDefaultLoader());
            string cookies;
            string[] versions;

            // Try to login.
            try
            {
                var csrfToken = await FetchCsrfTokensAsync(context, stoppingToken);
                cookies = await LoginAsync(context, csrfToken, stoppingToken);
                versions = await GetVersionsAsync(context, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to login Foundry VTT website with: {username}", options.Username);
                return;
            }

            using var client = ConfigureDownloadClient(cookies);

            if (client is null)
            {
                logger.LogError("Failed to initialize HTTP client for download with: {cookies}", cookies);
                return;
            }

            // Check file exists and download & upload to S3.
            using var scope = provider.CreateScope();
            using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            foreach (var version in versions)
            {
                foreach (var platform in platforms)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Cancellation token requested, stopping...");
                        return;
                    }

                    try
                    {
                        await ProcessReleaseAsync(platform, version, client, storage, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process release: {ver} of {plat}", version, platform);
                    }
                }
            }
        }

        private async Task ProcessReleaseAsync(string platform,
            string version,
            HttpClient client,
            IBlobStorage storage,
            CancellationToken stoppingToken = default)
        {
            var filePath = StoragePath.Combine(options.StorageDir, platform, $"foundryvtt-{version}.{platform.GetFileExtension()}");

            // Do not check hash.
            if (await storage.ExistsAsync(filePath, stoppingToken))
            {
                logger.LogTrace("Release file already exists at: {path}", filePath);
                // Skip if duplicated.
                return;
            }

            // Download.
            await using var fileStream = await DownloadReleaseAsync(client, version, platform, stoppingToken);

            // Upload with timeout set (linked to the stoppingToken).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(options.UploadTimeout * 1000);

            try
            {
                await storage.WriteAsync(filePath, fileStream, false, timeoutCts.Token);
                logger.LogInformation("Uploaded the release file to: {file}", filePath);
            }
            catch (OperationCanceledException oce)
            {
                logger.LogError(oce, "Failed to upload files to the storage due to timeout for: {file}", filePath);
            }
        }

        private async Task<string> FetchCsrfTokensAsync(IBrowsingContext context, CancellationToken stoppingToken = default)
        {
            IHtmlInputElement element;

            try
            {
                using var doc = await context.OpenAsync("https://foundryvtt.com", stoppingToken);
                element = (IHtmlInputElement) doc.QuerySelector("input[name=\"csrfmiddlewaretoken\"]");
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to select the CSRF control");
                throw new InvalidOperationException("Failed to get specified HTML value", e);
            }

            logger.LogInformation("Homepage loaded and CSRF control selected");
            return element.Value;
        }

        private async Task<string> LoginAsync(IBrowsingContext context, string csrfToken, CancellationToken stoppingToken = default)
        {
            logger.LogInformation("Logging in as: {username}", options.Username);

            // Construct POST body content.
            var loginBody = $"csrfmiddlewaretoken={UrlEncoder.Default.Encode(csrfToken)}&login_redirect={UrlEncoder.Default.Encode("/")}" +
                            $"&login_username={UrlEncoder.Default.Encode(options.Username)}&login_password={UrlEncoder.Default.Encode(options.Password)}&login=";
            await using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(loginBody));

            var loginRequest = new DocumentRequest(Url.Create("https://foundryvtt.com/auth/login"))
            {
                Method = AngleSharp.Io.HttpMethod.Post,
                Headers =
                {
                    { "DNT", "1" },
                    { "Upgrade-Insecure-Requests", "1" },
                    { "User-Agent", "Mozilla/5.0" }
                },
                Referer = "https://foundryvtt.com",
                MimeType = "application/x-www-form-urlencoded",
                Body = bodyStream
            };

            try
            {
                using var doc = await context.OpenAsync(loginRequest, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to POST login request with: {token}", csrfToken);
                throw new ArgumentException("Login failed", nameof(csrfToken));
            }

            var cookies = context.GetCookie(Url.Create("https://foundryvtt.com"));

            // ReSharper disable once StringLiteralTypo
            if (string.IsNullOrEmpty(cookies) || !cookies.Contains("sessionid", StringComparison.OrdinalIgnoreCase))
            {
                // ReSharper disable once StringLiteralTypo
                logger.LogError("Failed to get sessionid in: {cookies}", cookies);
                throw new InvalidOperationException("Cookies content is invalid");
            }

            logger.LogInformation("Logged in as: {username}", options.Username);
            return cookies;
        }

        private async Task<string[]> GetVersionsAsync(IBrowsingContext context, CancellationToken stoppingToken = default)
        {
            string[] versions;
            var trimLen = "https://foundryvtt.com/releases/".Length;

            try
            {
                using var doc = await context.OpenAsync("https://foundryvtt.com/releases/", stoppingToken);
                versions = doc.QuerySelectorAll("#releases-directory li.article .article-title a")
                    .OfType<IHtmlAnchorElement>().Select(e => e.Href[trimLen..])
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Where(x => x.VersionGte(options.Minimum))
                    .ToArray();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to fetch versions list in the release page as: {username}", options.Username);
                throw new InvalidOperationException("Invalid HTML query selection", e);
            }

            if (!versions.Any())
            {
                logger.LogError("No available versions in the release page from: {minVer}", options.Minimum);
                // Skip.
                // throw new InvalidOperationException("Array count is invalid");
            }

            logger.LogInformation("Got available versions in total: {count}", versions.Length);
            return versions;
        }

        private HttpClient ConfigureDownloadClient(string cookies)
        {
            // Set cookies for download.
            var cookieCon = new CookieContainer();
            foreach (var c in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    cookieCon.SetCookies(new Uri("https://foundryvtt.com"), c);
                }
                catch (CookieException ce)
                {
                    logger.LogError(ce, "Failed to set cookie for Foundry VTT website with: {cookie}", c);
                    return null;
                }
            }

            // To download manually, needs to prevent auto redirection.
            // The handler is disposed when the client is disposed.
            var handler = new HttpClientHandler { CookieContainer = cookieCon, AllowAutoRedirect = false };

            var client = new HttpClient(handler) { BaseAddress = new Uri("https://foundryvtt.com") };
            client.DefaultRequestHeaders.Referrer = new Uri("https://foundryvtt.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");

            logger.LogTrace("Ready to download Foundry VTT releases with: {cookies}", cookies);
            return client;
        }

        private async Task<Stream> DownloadReleaseAsync(HttpClient client, string version, string platform, CancellationToken stoppingToken = default)
        {
            var downloadUrl = $"/releases/download?version={version}&platform={platform}";
            using var resp = await client.GetAsync(downloadUrl, stoppingToken);

            var sc = (int) resp.StatusCode;
            if (sc < 300 || sc >= 400)
            {
                logger.LogError("Failed to parse URL: {url} with {code}", downloadUrl, resp.StatusCode);
                return null;
            }

            // Redirect to download.
            var dlUri = resp.Headers.Location;
            if (dlUri == null || !Uri.IsWellFormedUriString(dlUri.ToString(), UriKind.Absolute))
            {
                logger.LogError("Failed to redirect to: {uri}", dlUri);
                return null;
            }

            var tmpFileName = Path.GetTempFileName();
            var fileStream = File.Open(tmpFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            try
            {
                using var dlResponse = await client.GetAsync(dlUri, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                await using var stream = await dlResponse.Content.ReadAsStreamAsync(stoppingToken);
                await stream.CopyToAsync(fileStream, stoppingToken);
            }
            catch (Exception e)
            {
                await fileStream.DisposeAsync();
                logger.LogError(e, "Failed to download to local temp file for: {ver} of {plat}", version, platform);
            }

            if (options.TrimLinuxPackage && platform.Equals("linux") && await TrimLinuxPackageAsync(fileStream, stoppingToken))
            {
                var length = fileStream.Length / (1024 * 1024);
                await fileStream.DisposeAsync();
                fileStream = File.Open(tmpFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                logger.LogInformation("Linux package trimmed unnecessary files for: {ver}, {oldSize}M => {newSize}M", version, length, fileStream.Length);
            }

            // 1M as a check.
            if (fileStream == null || fileStream.Length < 1024 * 1024)
            {
                logger.LogError("Failed to download the release file from: {url}", downloadUrl);
                throw new InvalidOperationException("FileStream is invalid");
            }

            logger.LogTrace("Release file downloaded successfully from: {url}", downloadUrl);
            return fileStream;
        }

        private async Task<bool> TrimLinuxPackageAsync(Stream zipStream, CancellationToken stoppingToken = default)
        {
            // Open the zip file.
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Update, true);
            var trimmed = false;

            foreach (var entry in zip.Entries.Where(e => !e.FullName.StartsWith("resources/")).ToList())
            {
                entry.Delete();
                trimmed = true;
                logger.LogTrace("Linux package trimmed file at: {entry}", entry.FullName);
            }

            // Flush to the temp file.
            await zipStream.FlushAsync(stoppingToken);
            return trimmed;
        }
    }
}
