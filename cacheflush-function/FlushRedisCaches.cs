using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Extensions.Logging;

using Microsoft.Azure.Services.AppAuthentication;
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

        [FunctionName(nameof(ServieBusTrigger))]
        public async Task ServieBusTrigger([ServiceBusTrigger("%SB_TOPIC_NAME%", "%SB_SUBSCRIPTION_NAME%", Connection = "ServiceBusConnectionString")] string queueItem,
        [DurableClient] IDurableOrchestrationClient starter,
         ILogger log)
        {
            string flushRepeats = Environment.GetEnvironmentVariable("FLUSH_REPEATS") ?? "1";

            var durableInstanceId = await starter.StartNewAsync(nameof(CacheFlushOrchestrator), input: flushRepeats);
            log.LogInformation("Started durable orchestrator with instanceId={instanceId}", durableInstanceId);
        }

        [FunctionName(nameof(CacheFlushOrchestrator))]
        public async Task CacheFlushOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log = context.CreateReplaySafeLogger(log);
            var counter = context.GetInput<int>();

            log.LogInformation($"{nameof(CacheFlushOrchestrator)} received a call with counter={counter}");

            if (counter > 0)
            {
                log.LogInformation("Calling activity function");
                await context.CallActivityAsync(nameof(FlushCachesActivity), "UK South,UK West");
                log.LogInformation($"Activity function finished");

                string delay = Environment.GetEnvironmentVariable("REPEAT_DELAY_SECONDS") ?? "30";
                var nextRun = context.CurrentUtcDateTime.AddSeconds(int.Parse(delay));
                counter--;
                if (counter > 0)
                {
                    log.LogInformation($"Next run will start at {nextRun}. {counter} runs remaining");
                    await context.CreateTimer(nextRun, CancellationToken.None);
                    context.StartNewOrchestration(nameof(CacheFlushOrchestrator), counter--);
                }
                else
                {
                    log.LogInformation("Orchestrator done. No more runs left.");
                }
            }
        }

        [FunctionName(nameof(FlushCachesActivity))]
        public async Task FlushCachesActivity(
            [ActivityTrigger] string regions, ILogger log)
        {
            log.LogInformation("Function {name} was triggered for environment {environment}", nameof(FlushCachesActivity), environment);
            try
            {
                log.LogInformation("Starting cache flush for environment {environment} in regions {region}", environment, regions);
                var result = await FlushCaches(environment, regions, log);
            }
            catch (Exception e)
            {
                log.LogError(e, "Exception during processing");
            }
        }

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
                var azure = Microsoft.Azure.Management.Fluent.Azure
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
                var keysRemoved = 0;
                
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
                        
                        if (server.IsReplica)
                        {
                            continue;
                        }

                        var swFlush = Stopwatch.StartNew();
                        await server.FlushDatabaseAsync(0);
                        swFlush.Stop();
                        var flushTracker = new DependencyTelemetry
                        {
                            Name = "FLUSHALL",
                            Type = "Redis",
                            Target = endpointName, // for some reason the endpoint hostname starts with "unspecified/..."
                            Duration = swFlush.Elapsed,
                            Success = true
                        };
                        this.telemetryClient.TrackDependency(flushTracker);
                        count++;

                        // Clear non-area records
                        var swDelete = Stopwatch.StartNew();
                        var database = connection.GetDatabase(2);
                        var keys = server.KeysAsync(2, "[^area]*", 200);
                        List<Task> deleteTasks = new List<Task>();
                        IBatch batch = database.CreateBatch();
                        await foreach (var key in keys)
                        {
                            Task<bool> delTask = batch.KeyDeleteAsync(key, CommandFlags.FireAndForget);
                            deleteTasks.Add(delTask);
                            keysRemoved++;
                        }
                        batch.Execute();
                        var tasks = deleteTasks.ToArray();
                        Task.WaitAll(tasks);
                        swDelete.Stop();
                        var deleteTracker = new DependencyTelemetry
                        {
                            Name = "DEL",
                            Type = "Redis",
                            Target = endpointName,
                            Duration = swDelete.Elapsed,
                            Success = true
                        };
                        this.telemetryClient.TrackDependency(deleteTracker);

                    }
                }
                log.LogInformation(
                    "Successfully flushed {count} caches in db0 and cleared {keysRemoved} keys from db2 for " +
                            "environment={environment}", 
                    count, keysRemoved, environment
                    );
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
