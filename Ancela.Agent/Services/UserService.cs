using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ancela.Agent.Services;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UserProfile
{
    public Guid Id { get; set; }
    public required string AgentPhoneNumber { get; set; }
    public required string UserPhoneNumber { get; set; }
    public string? Name { get; set; }       // null = registration pending
    public string? TimeZone { get; set; }   // IANA tz id, e.g. "America/Los_Angeles"; null = pending
    public string? Location { get; set; }   // human-readable home location, e.g. "Seattle, WA"; null = not captured
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
}

public interface IUserService
{
    Task<UserProfile> CreatePendingAsync(string agentPhoneNumber, string userPhoneNumber);
    Task<UserProfile?> GetAsync(string agentPhoneNumber, string userPhoneNumber);
    Task CompleteRegistrationAsync(string agentPhoneNumber, string userPhoneNumber, string name, string timeZone, string location);
    Task DeleteAsync(string agentPhoneNumber, string userPhoneNumber);
}

public class UserService(CosmosClient _cosmosClient) : IUserService
{
    private const string DatabaseName = "anceladb";
    private const string ContainerName = "users";

    private async Task<Container> GetContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(
            ContainerName,
            "/agentPhoneNumber");
        return containerResponse.Container;
    }

    public async Task<UserProfile> CreatePendingAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var profile = new UserProfile
        {
            Id = Guid.NewGuid(),
            AgentPhoneNumber = agentPhoneNumber,
            UserPhoneNumber = userPhoneNumber,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await container.CreateItemAsync(profile, new PartitionKey(agentPhoneNumber));
        return profile;
    }

    public async Task<UserProfile?> GetAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.agentPhoneNumber = @agentPhoneNumber AND c.userPhoneNumber = @userPhoneNumber")
            .WithParameter("@agentPhoneNumber", agentPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);
        var iterator = container.GetItemQueryIterator<UserProfile>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            if (response.Any())
                return response.First();
        }
        return null;
    }

    public async Task CompleteRegistrationAsync(string agentPhoneNumber, string userPhoneNumber, string name, string timeZone, string location)
    {
        var container = await GetContainerAsync();
        var profile = await GetAsync(agentPhoneNumber, userPhoneNumber)
            ?? throw new InvalidOperationException($"No pending profile for {userPhoneNumber}");
        profile.Name = name;
        profile.TimeZone = timeZone;
        profile.Location = location;
        profile.RegisteredAt = DateTimeOffset.UtcNow;
        await container.ReplaceItemAsync(profile, profile.Id.ToString(), new PartitionKey(agentPhoneNumber));
    }

    public async Task DeleteAsync(string agentPhoneNumber, string userPhoneNumber)
    {
        var container = await GetContainerAsync();
        var profile = await GetAsync(agentPhoneNumber, userPhoneNumber);
        if (profile != null)
            await container.DeleteItemAsync<UserProfile>(profile.Id.ToString(), new PartitionKey(agentPhoneNumber));
    }
}
