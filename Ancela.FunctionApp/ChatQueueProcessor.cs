using Ancela.Agent;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Processes chat messages from the queue.
/// </summary>
public class ChatQueueProcessor(ILogger<ChatQueueProcessor> _logger, ChatInterceptor _chatInterceptor, SmsService _smsService)
{
    [Function(nameof(ChatQueueProcessor))]
    public async Task Run([QueueTrigger(ChatQueueMessage.QueueName, Connection = "queues")] ChatQueueMessage message)
    {
        _logger.LogInformation("Processing message from queue: {Message}", message.Content);

        var reply = await _chatInterceptor.HandleMessage(
            message.Content,
            message.UserPhoneNumber,
            message.AgentPhoneNumber,
            message.MediaUrls);

        if (reply != null)
            await _smsService.Send(message.UserPhoneNumber, reply);

        _logger.LogInformation("Successfully processed message from queue");
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