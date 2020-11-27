using System;
using System.IO;
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

using StackExchange.Redis;
using Microsoft.Rest;

namespace Coronavirus.CacheFlush
{
    public static class FlushRedisCaches
    {
        [FunctionName("FlushRedisCaches")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            ILogger log)
        {
            try
            {
                string environment = req.Query["environment"];

                log.LogInformation($"{nameof(FlushRedisCaches)} HTTP trigger function received a request to flush cached in env={environment}");

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
                var caches = (await azure.RedisCaches.ListAsync()).Where(c => c.Tags.Any(t => t.Key == "C19-Environment" && t.Value == environment));

                if(caches == null || caches.Count() == 0)
                {
                    log.LogError($"No caches found for environment={environment}");
                    return new BadRequestObjectResult($"No caches found for environment={environment}");
                }

                var count = 0;

                foreach (var cache in caches)
                {
                    var redisAccessKeys = cache.GetKeys();

                    ConnectionMultiplexer connection =
                    ConnectionMultiplexer.Connect($"{cache.HostName}:6380,abortConnect=false,ssl=True,allowAdmin=true,password={redisAccessKeys.PrimaryKey}");

                    foreach (var ep in connection.GetEndPoints())
                    {
                        log.LogInformation($"Flushing database on server {ep}");
                        var server = connection.GetServer(ep);
                        if (!server.IsReplica)
                        {
                            await server.FlushAllDatabasesAsync();
                            count++;
                        }
                    }
                }

                log.LogInformation($"Finished flushing {count} caches for environment={environment}");
                return new OkObjectResult($"Successfully flushed {count} caches for environment={environment}");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error during flushing caches for environment={environment}");
                return new StatusCodeResult(500);
            }
        }
    }
}
