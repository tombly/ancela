using System.Text.Json;
using Azure.Storage.Queues;

namespace Ancela.Agent.SemanticKernel.Plugins.ChatPlugin;

public interface ILoopbackService
{
    Task SendMessageToSelf(string userPhoneNumber, string agentPhoneNumber, string content, decimal? delayHours = null);
}

/// <summary>
/// Service for sending messages to oneself via a queue for autonomous agent behavior.
/// </summary>
public class LoopbackService(QueueServiceClient _queueServiceClient) : ILoopbackService
{
    public async Task SendMessageToSelf(string userPhoneNumber, string agentPhoneNumber, string content, decimal? delayHours = null)
    {
        var queueMessage = new ChatQueueMessage
        {
            Content = content,
            UserPhoneNumber = userPhoneNumber,
            AgentPhoneNumber = agentPhoneNumber,
            MediaUrls = []
        };

        var queueClient = _queueServiceClient.GetQueueClient(ChatQueueMessage.QueueName);
        await queueClient.CreateIfNotExistsAsync();

        var messageBytes = JsonSerializer.SerializeToUtf8Bytes(queueMessage);
        var base64Message = Convert.ToBase64String(messageBytes);

        if (delayHours.HasValue && delayHours.Value > 0)
        {
            var visibilityTimeout = TimeSpan.FromHours((double)delayHours.Value);
            await queueClient.SendMessageAsync(base64Message, visibilityTimeout: visibilityTimeout);
        }
        else
        {
            await queueClient.SendMessageAsync(base64Message);
        }
    }
}

public record ChatQueueMessage
{
    public const string QueueName = "chat-queue";

    public string Content { get; init; } = string.Empty;
    public string UserPhoneNumber { get; init; } = string.Empty;
    public string AgentPhoneNumber { get; init; } = string.Empty;
    public string[] MediaUrls { get; init; } = [];
}
