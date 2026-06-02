using System.Text.Json;
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
    public async Task Run([ServiceBusTrigger(ChatQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ChatQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize chat queue message: {body}");

        _logger.LogInformation("Processing message from queue: {Message}", message.Content);

        var reply = await _chatInterceptor.HandleMessage(
            message.Content,
            message.UserPhoneNumber,
            message.AgentPhoneNumber,
            message.Media);

        if (reply != null)
            await _smsService.Send(message.UserPhoneNumber, reply);

        _logger.LogInformation("Successfully processed message from queue");
    }
}

public record ChatQueueMessage
{
    public const string QueueName = "chat-messages";

    public string Content { get; init; } = string.Empty;
    public string UserPhoneNumber { get; init; } = string.Empty;
    public string AgentPhoneNumber { get; init; } = string.Empty;
    public Media[] Media { get; init; } = [];
}
