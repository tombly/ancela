using Microsoft.Azure.Cosmos;

namespace Ancilla.FunctionApp.Services;

/// <summary>
/// Service for managing users in Cosmos DB. Entries are partitioned by the AI's
/// phone number so that all users for a given AI are stored together.
/// </summary>
public class UserService(CosmosClient _cosmosClient)
{
    private const string DatabaseName = "ancilladb";
    private const string ContainerName = "users";

    public async Task CreateUserAsync(string aiPhoneNumber, string userPhoneNumber, List<string>? invitedUserIds = null)
    {
        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);

        var userEntry = new
        {
            id = Guid.NewGuid(),
            aiPhoneNumber,
            userPhoneNumber,
            invitedUserIds = invitedUserIds ?? []
        };

        await container.CreateItemAsync(userEntry, new PartitionKey(aiPhoneNumber));
    }

    public async Task<UserEntry?> GetUserAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber AND c.userPhoneNumber = @userPhoneNumber")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber)
            .WithParameter("@userPhoneNumber", userPhoneNumber);

        var iterator = container.GetItemQueryIterator<UserEntry>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            if (response.Any())
            {
                return response.First();
            }
        }

        return null;
    }

    public async Task<UserEntry[]> GetAllUsersAsync(string aiPhoneNumber)
    {
        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.aiPhoneNumber = @aiPhoneNumber")
            .WithParameter("@aiPhoneNumber", aiPhoneNumber);

        var iterator = container.GetItemQueryIterator<UserEntry>(query);
        var entries = new List<UserEntry>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            entries.AddRange(response);
        }

        return [.. entries];
    }

    public async Task UpdateInvitedUserIdsAsync(string aiPhoneNumber, string userPhoneNumber, List<string> invitedUserIds)
    {
        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);
        var user = await GetUserAsync(aiPhoneNumber, userPhoneNumber);

        if (user == null)
        {
            throw new InvalidOperationException($"User not found: {userPhoneNumber}");
        }

        var updatedUser = new
        {
            id = user.Id,
            aiPhoneNumber = user.AiPhoneNumber,
            userPhoneNumber = user.UserPhoneNumber,
            invitedUserIds
        };

        await container.ReplaceItemAsync(updatedUser, user.Id.ToString(), new PartitionKey(aiPhoneNumber));
    }

    public async Task DeleteUserAsync(string aiPhoneNumber, string userPhoneNumber)
    {
        var container = _cosmosClient.GetDatabase(DatabaseName).GetContainer(ContainerName);
        var user = await GetUserAsync(aiPhoneNumber, userPhoneNumber);

        if (user != null)
            await container.DeleteItemAsync<UserEntry>(user.Id.ToString(), new PartitionKey(aiPhoneNumber));
    }
}

public class UserEntry
{
    public Guid Id { get; set; }
    public string AiPhoneNumber { get; set; } = string.Empty;
    public string UserPhoneNumber { get; set; } = string.Empty;
    public string[] InvitedUserIds { get; set; } = [];
}