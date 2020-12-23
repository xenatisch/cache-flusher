using System;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Rest;
using StackExchange.Redis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coronavirus.CacheFlush
{
    public class FlushRedisCaches
    {

        private readonly TelemetryClient telemetryClient;

        /// Using dependency injection will guarantee that you use the same configuration for telemetry collected automatically and manually.
        public FlushRedisCaches(TelemetryConfiguration telemetryConfiguration)
        {
            this.telemetryClient = new TelemetryClient(telemetryConfiguration);
        }

        static string environment = Environment.GetEnvironmentVariable("ENVIRONMENT");

        [FunctionName(nameof(FlushCachesInEnvironment))]
        public async Task<IActionResult> FlushCachesInEnvironment(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation($"{nameof(FlushCachesInEnvironment)} HTTP trigger function received a request to flush caches in env={environment}");

            var result = await FlushCaches(environment, "UK South,UK West", log);
            if (result.success)
            {
                return new OkObjectResult(result.message);
            }
            else
            {
                return new StatusCodeResult(500);
            }
        }

        private const string TimestampBlobPath = "publicdata/assets/dispatch/website_timestamp";

        [FunctionName(nameof(BlobTriggerPrimaryRegion))]
        public async Task BlobTriggerPrimaryRegion([BlobTrigger(TimestampBlobPath, Connection = "DataStorageConnectionstringPrimary")] CloudBlockBlob blob, ILogger log)
        {
            await BlobTriggerInternal(blob, nameof(BlobTriggerPrimaryRegion), "UK South", log);
        }

        [FunctionName(nameof(BlobTriggerSecondaryRegion))]
        public async Task BlobTriggerSecondaryRegion([BlobTrigger(TimestampBlobPath, Connection = "DataStorageConnectionstringSecondary")] CloudBlockBlob blob, ILogger log)
        {
            await BlobTriggerInternal(blob, nameof(BlobTriggerSecondaryRegion), "UK West", log);
        }

        private async Task BlobTriggerInternal(CloudBlockBlob blob, string functionName, string region, ILogger log)
        {
            log.LogInformation("Function {name} was triggered for environment {environment} change of blob {blobUri}", functionName, environment, blob.Uri);
            try
            {
                log.LogInformation("Starting cache flush for environment {environment} in region {region}", environment, region);
                var result = await FlushCaches(environment, region, log);
            }
            catch (Exception e)
            {
                log.LogError(e, "Exception during processing");
            }
        }

        /*
        ** Commented out for now as EventGrid does not work currently with Object-replicated storage accounts

                [FunctionName(nameof(BlobEventTrigger))]
                public async Task BlobEventTrigger([EventGridTrigger] JObject eventGridEvent, ILogger log)
                {
                    log.LogInformation(eventGridEvent.ToString(Formatting.Indented));
                    dynamic eventJson = (dynamic)eventGridEvent;
                    try
                    {
                        string topic = eventJson.topic;
                        string eventType = eventJson.eventType;
                        string blobPath = eventJson.subject;

                        if (!topic.Contains("Microsoft.Storage/storageAccounts"))
                        {
                            log.LogWarning("Wrong event topic {topic}. Expected Microsoft.Storage/storageAccounts", topic);
                            return;
                        }

                        if (eventType != "Microsoft.Storage.BlobCreated")
                        {
                            log.LogWarning("Wrong event type {eventType}. Expected Microsoft.Storage.BlobCreated", eventType);
                            return;
                        }

                        const string expectedBlobName = "assets/dispatch/website_timestamp";
                        if (!blobPath.EndsWith(expectedBlobName))
                        {
                            log.LogInformation("Wrong blob {blob}. Only reacting on changes to {blobName} file", blobPath, expectedBlobName);
                            return;
                        }

                        var storageAccountName = topic.Split('/').Last();

                        const string pattern = "/resourceGroups/.*-.*-(?<env>.*)/providers";
                        var regex = new Regex(pattern);
                        var matches = regex.Match(topic);
                        if (!string.IsNullOrEmpty(matches.Groups["env"]?.Value))
                        {
                            var environment = matches.Groups["env"].Value;
                            log.LogInformation("Starting cache flush for environment {environment}", environment);
                            var result = await FlushCache(environment, log);
                            if (result.success)
                            {
                                log.LogInformation(result.message);
                            }
                            else
                            {
                                log.LogError(result.message);
                            }
                        }
                        else
                        {
                            log.LogWarning("Could not parse environment name from topic {topic}. Ignoring event.", topic);
                        }
                    }
                    catch (Exception e)
                    {
                        log.LogError(e, "Exception during processing");
                    }
                }
        */

        private async Task<(bool success, string message)> FlushCaches(string environment, string regions, ILogger log)
        {
            try
            {
                log.LogInformation("Acquiring AAD token");
                // Get auth token through MSI
                var tenantId = Environment.GetEnvironmentVariable("tenantId");
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                var token = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com", tenantId);
                var tokenCredentials = new TokenCredentials(token);
                log.LogInformation("Got AAD token. Creating Azure client");
                var azure = Azure
                    .Configure()
                    .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                    .Authenticate(new AzureCredentials(tokenCredentials, tokenCredentials, tenantId, AzureEnvironment.AzureGlobalCloud))
                    .WithDefaultSubscription();

                log.LogInformation($"Listing caches by tag 'C19-Environment'={environment} in regions={regions}");

                // List Redis Caches and select by tag for the environment
                var caches = (await azure.RedisCaches.ListAsync())?.Where(c => c.Tags.Any(tag => tag.Key == "C19-Environment" && tag.Value == environment) && regions.Contains(c.RegionName, StringComparison.InvariantCultureIgnoreCase));

                if (caches == null || caches.Count() == 0)
                {
                    log.LogWarning("No caches found for environment={environment} in regions={regions}", environment, regions);
                    return (false, $"No caches found for environment={environment} in regions={regions}");
                }

                var count = 0;

                foreach (var cache in caches)
                {
                    var redisAccessKeys = cache.GetKeys();

                    ConnectionMultiplexer connection =
                    ConnectionMultiplexer.Connect($"{cache.HostName}:6380,abortConnect=false,ssl=True,allowAdmin=true,password={redisAccessKeys.PrimaryKey}");

                    foreach (var endpoint in connection.GetEndPoints())
                    {
                        var endpointName = endpoint.ToString().Replace("Unspecified/", "");
                        log.LogInformation("Flushing database on server {endpoint}", endpointName);
                        var server = connection.GetServer(endpoint);
                        if (!server.IsReplica)
                        {
                            var sw = Stopwatch.StartNew();
                            await server.FlushAllDatabasesAsync();
                            sw.Stop();
                            var dependency = new DependencyTelemetry
                            {
                                Name = "FLUSHALL",
                                Type = "Redis",
                                Target = endpointName, // for some reason the endpoint hostname starts with "unspecified/..."
                                Duration = sw.Elapsed,
                                Success = true
                            };
                            this.telemetryClient.TrackDependency(dependency);
                            count++;
                        }
                    }
                }
                log.LogInformation("Successfully flushed {count} caches for environment={environment}", count, environment);
                return (true, $"Successfully flushed {count} caches for environment={environment}");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error during flushing caches for environment={environment}", environment);
                return (false, $"Error during flushing caches for environment={environment}");
            }
        }
    }
}
