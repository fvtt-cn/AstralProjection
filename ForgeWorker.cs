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

        private readonly string[] platforms;
        private static readonly string[] SupportedPlatforms = { "linux", "windows", "mac" };

        public ForgeWorker(IServiceProvider serviceProvider, ForgeOptions opts, ILogger<ForgeWorker> lgr)
        {
            provider = serviceProvider;
            options = opts;

            schedule = CrontabSchedule.Parse(opts.Schedule);
            nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
            logger = lgr;

            // Default all platforms.
            platforms = options.Platforms ?? SupportedPlatforms;
            platforms = platforms.Where(x => SupportedPlatforms.Contains(x)).ToArray();

            if (string.IsNullOrEmpty(options.Username) || string.IsNullOrEmpty(options.Password) ||
                !options.Minimum.IsVersion() || !platforms.Any())

            {
                logger.LogError($"Configuration for {nameof(ForgeWorker)} is invalid.");
                throw new ArgumentException(nameof(opts));
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
                    logger.LogInformation($"{nameof(ForgeWorker)} triggered.");
                    await ProcessAsync(stoppingToken).ConfigureAwait(false);

                    nextRunDate = schedule.GetNextOccurrence(DateTime.Now);
                }

                // Wait 5s to check.
                await Task.Delay(GlobalSettings.CHECK_DELAY_MS, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            // Firstly, try to login.
            logger.LogInformation($"Logging in as {options.Username}");

            var config = Configuration.Default.WithDefaultCookies().WithDefaultLoader();
            using var context = BrowsingContext.New(config);

            var csrfToken = await FetchCsrfTokensAsync(context, stoppingToken);
            if (string.IsNullOrEmpty(csrfToken))
            {
                logger.LogError("Fetched CSRF token is empty and invalid.");
                return;
            }

            logger.LogInformation("Homepage loaded.");

            var cookies = await LoginAsync(context, csrfToken, stoppingToken);
            if (string.IsNullOrEmpty(cookies) || !cookies.Contains("sessionid", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError($"Fetched Cookies {cookies} is invalid.");
                return;
            }

            logger.LogInformation("Site logged in.");

            // Secondly, get current versions list by extracting release notes page.
            var versions = await GetVersionsAsync(context, stoppingToken) ?? Array.Empty<string>();
            versions = versions.Where(x => x.VersionGte(options.Minimum)).ToArray() ;

            if (!versions.Any())
            {
                logger.LogError("No available versions.");
                return;
            }

            logger.LogInformation($"Fetched {versions.Length} available versions.");

            // Then set cookies for download.
            var cookieCon = new CookieContainer();
            foreach (var c in cookies.Split(';'))
            {
                cookieCon.SetCookies(new Uri("https://foundryvtt.com"), c);
            }

            // To download manually, needs to prevent auto redirection.
            using var handler = new HttpClientHandler { CookieContainer = cookieCon, AllowAutoRedirect = false };

            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://foundryvtt.com") };
            client.DefaultRequestHeaders.Referrer = new Uri("https://foundryvtt.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");

            // Final step, check file exists and download & upload to S3.
            using var scope = provider.CreateScope();
            using var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            foreach (var version in versions)
            {
                foreach (var platform in platforms)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Cancellation token requested, stopping...");
                        break;
                    }

                    var filePath = StoragePath.Combine(options.StorageDir, platform,
                        $"foundryvtt-{version}.{GetFileExtension(platform)}");

                    // Do not check hash.
                    if (await storage.ExistsAsync(filePath, stoppingToken))
                    {
                        logger.LogInformation($"File '{filePath}' already exists.");
                        continue;
                    }

                    // Download.
                    await using var fileStream =
                        await DownloadAsync(client, version, platform, stoppingToken);

                    // > 1M at least.
                    if (fileStream != null && fileStream.Length > 1024 * 1024)
                    {
                        // Upload.
                        await storage.WriteAsync(filePath, fileStream, false, stoppingToken);
                        logger.LogInformation($"File '{filePath}' uploaded.");
                    }
                }
            }

            logger.LogInformation("Forge process completed.");
        }

        private static string GetFileExtension(string platform) => platform.ToUpper() switch
        {
            "WINDOWS" => "exe",
            "MAC" => "dmg",
            "LINUX" => "zip",
            _ => "zip"
        };

        private async Task<string> FetchCsrfTokensAsync(IBrowsingContext context,
            CancellationToken stoppingToken = default)
        {
            string value = null;

            try
            {
                using var doc = await context.OpenAsync("https://foundryvtt.com", stoppingToken);

                var inputElement = doc.QuerySelector("input[name=\"csrfmiddlewaretoken\"]");
                if (inputElement is IHtmlInputElement input)
                {
                    value = input.Value;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to fetch CSRF token.");
            }

            return value;
        }

        private async Task<string> LoginAsync(IBrowsingContext context,
            string csrfToken,
            CancellationToken stoppingToken = default)
        {
            // Construct POST body content.
            var loginBody =
                $"csrfmiddlewaretoken={UrlEncoder.Default.Encode(csrfToken)}&login_redirect={UrlEncoder.Default.Encode("/")}" +
                $"&login_username={UrlEncoder.Default.Encode(options.Username)}&login_password={UrlEncoder.Default.Encode(options.Password)}&login=";
            var bodyBytes = Encoding.UTF8.GetBytes(loginBody);
            await using var bodyStream = new MemoryStream(bodyBytes);

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
                return context.GetCookie(Url.Create("https://foundryvtt.com"));
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Failed to login account '{options.Username}'.");
            }

            return null;
        }

        private async Task<string[]> GetVersionsAsync(IBrowsingContext context,
            CancellationToken stoppingToken = default)
        {
            try
            {
                using var doc = await context.OpenAsync("https://foundryvtt.com/releases/", stoppingToken);

                var trimLen = "https://foundryvtt.com/releases/".Length;
                var refs = doc.QuerySelectorAll("#releases-directory li.article .article-title a")
                    .OfType<IHtmlAnchorElement>().Select(e => e.Href.Substring(trimLen))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray();

                return refs;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to fetch versions list.");
            }

            return null;
        }

        private async Task<Stream> DownloadAsync(HttpClient client,
            string version,
            string platform,
            CancellationToken stoppingToken = default)
        {
            var downloadUrl = $"/releases/download?version={version}&platform={platform}";
            using var resp = await client.GetAsync(downloadUrl, stoppingToken);

            var sc = (int) resp.StatusCode;
            if (sc < 300 || sc >= 400)
            {
                logger.LogError($"Failed to parse '{downloadUrl}' with status code {resp.StatusCode}.");
                return null;
            }

            // Redirect to download.
            var dlUri = resp.Headers.Location;
            if (!Uri.IsWellFormedUriString(dlUri.ToString(), UriKind.Absolute))
            {
                logger.LogError($"Failed to redirect to '{dlUri}'.");
                return null;
            }

            try
            {
                var tempFilePath = Path.GetTempFileName();
                var fileStream = File.Open(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

                // Copy without memory footprint.
                using var dlResp = await client.GetAsync(dlUri, HttpCompletionOption.ResponseHeadersRead, stoppingToken);

                await using var stream = await dlResp.Content.ReadAsStreamAsync();
                await stream.CopyToAsync(fileStream, stoppingToken);

                return fileStream;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Failed to download {platform}/{version} to local temp file.");
            }

            return null;
        }
    }
}
