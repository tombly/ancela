using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ancilla.FunctionApp;

public class CosmosDbInitializer(CosmosClient _cosmosClient, ILogger<CosmosDbInitializer> _logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing Cosmos DB...");

        const int maxRetries = 20;
        const int delayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
                    "ancilladb", cancellationToken: cancellationToken);

                await database.Database.CreateContainerIfNotExistsAsync(
                    "notes", "/partitionKey", cancellationToken: cancellationToken);

                await database.Database.CreateContainerIfNotExistsAsync(
                    "history", "/userPhoneNumber", cancellationToken: cancellationToken);

                _logger.LogInformation("Cosmos DB initialization complete.");
                return;
            }
            catch (Exception ex)
            {
                if (i < maxRetries - 1)
                {
                    _logger.LogWarning("Cosmos DB not ready yet (attempt {Attempt}/{MaxRetries}): {Message}",
                        i + 1, maxRetries, ex.Message);
                    await Task.Delay(delayMs, cancellationToken);
                }
                else
                {
                    _logger.LogError(ex, "Failed to initialize Cosmos DB after {MaxRetries} retries", maxRetries);
                    throw new InvalidOperationException("Failed to initialize Cosmos DB after maximum retries.", ex);
                }
            }
        }

        // This line is now unreachable, as the final exception is thrown in the catch block above.
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}