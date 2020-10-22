using AstralProjection.Options;
using COSSTS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Storage.Net;
using Storage.Net.Blobs;
using System;
using System.Collections.Generic;

namespace AstralProjection.Services
{
    public static class BlobStorageServiceExtensions
    {
        public static IServiceCollection AddQCloudStorage(this IServiceCollection services, Action<S3Options> setupAction)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (setupAction == null)
            {
                throw new ArgumentNullException(nameof(setupAction));
            }

            services.AddOptions();
            services.Configure(setupAction);

            services.TryAddScoped<IBlobStorage>(provider =>
            {
                var s3Options = provider.GetRequiredService<IOptions<S3Options>>();
                var options = s3Options.Value;

                // QCloud only as for now.
                var allowActions = new [] { "*" };
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
                if (result["Credentials"] is JToken credentials)
                {
                    // S3.
                    return StorageFactory.Blobs.AwsS3(
                        credentials.Value<string>("TmpSecretId"),
                        credentials.Value<string>("TmpSecretKey"),
                        credentials.Value<string>("Token"),
                        options.Bucket,
                        null,
                        options.ServiceUrl);
                }

                throw new ArgumentException("Can not initialize S3 storage.");
            });

            return services;
        }
    }
}
