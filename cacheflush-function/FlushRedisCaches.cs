using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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
using Microsoft.Azure.Storage.Blob;

namespace Coronavirus.CacheFlush
{
    public static class FlushRedisCaches
    {
        [FunctionName(nameof(FlushCachesInEnvironment))]
        public static async Task<IActionResult> FlushCachesInEnvironment(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            ILogger log)
        {
            string environment = "";

            environment = req.Query["environment"];

            log.LogInformation($"{nameof(FlushCachesInEnvironment)} HTTP trigger function received a request to flush cached in env={environment}");

            var result = await FlushCache(environment, log);
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

        [FunctionName(nameof(BlobTrigger))]
        public static async Task BlobTrigger([BlobTrigger(TimestampBlobPath, Connection = "DataStorageConnectionstring")] CloudBlockBlob blob, ILogger log)
        {
            var environment = Environment.GetEnvironmentVariable("ENVIRONMENT");
            log.LogInformation("Function {name} was triggered for environment {environment}  change of blob {blobUri}", nameof(BlobTrigger), environment, blob.Uri);
            try
            {
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
            catch (Exception e)
            {
                log.LogError(e, "Exception during processing");
            }
        }
        /*
        ** Commented out for now as EventGrid does not work currently with Object-replicated storage accounts

                [FunctionName(nameof(BlobEventTrigger))]
                public static async Task BlobEventTrigger([EventGridTrigger] JObject eventGridEvent, ILogger log)
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
        private static async Task<(bool success, string message)> FlushCache(string environment, ILogger log)
        {
            try
            {
                var tenantId = Environment.GetEnvironmentVariable("tenantId");
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                var token = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com", tenantId);
                var tokenCredentials = new TokenCredentials(token);
                var azure = Azure
                    .Configure()
                    .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                    .Authenticate(new AzureCredentials(tokenCredentials, tokenCredentials, tenantId, AzureEnvironment.AzureGlobalCloud))
                    .WithDefaultSubscription();

                // List Redis Caches and select by tag for the environment
                var caches = (await azure.RedisCaches.ListAsync())?.Where(c => c.Tags.Any(tag => tag.Key == "C19-Environment" && tag.Value == environment));

                if (caches == null || caches.Count() == 0)
                {
                    return (false, $"No caches found for environment={environment}");
                }

                var count = 0;

                foreach (var cache in caches)
                {
                    var redisAccessKeys = cache.GetKeys();

                    ConnectionMultiplexer connection =
                    ConnectionMultiplexer.Connect($"{cache.HostName}:6380,abortConnect=false,ssl=True,allowAdmin=true,password={redisAccessKeys.PrimaryKey}");

                    foreach (var endpoint in connection.GetEndPoints())
                    {
                        log.LogInformation("Flushing database on server {endpoint}", endpoint);
                        var server = connection.GetServer(endpoint);
                        if (!server.IsReplica)
                        {
                            await server.FlushAllDatabasesAsync();
                            count++;
                        }
                    }
                }

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
