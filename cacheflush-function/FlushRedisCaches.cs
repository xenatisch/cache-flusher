using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Rest;
using StackExchange.Redis;

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
            try
            {
                environment = req.Query["environment"];

                log.LogInformation($"{nameof(FlushCachesInEnvironment)} HTTP trigger function received a request to flush cached in env={environment}");

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

                if(caches == null || caches.Count() == 0)
                {
                    log.LogError("No caches found for environment={environment}", environment);
                    return new BadRequestObjectResult($"No caches found for environment={environment}");
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

                log.LogInformation("Finished flushing {count} caches for environment={environment}", count, environment);
                return new OkObjectResult($"Successfully flushed {count} caches for environment={environment}");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error during flushing caches for environment={environment}", environment);
                return new StatusCodeResult(500);
            }
        }
    }
}
