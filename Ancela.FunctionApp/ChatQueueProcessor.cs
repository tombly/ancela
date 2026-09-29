using System.Text.Json;
using Ancela.Agent;
using Ancela.Agent.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Processes chat messages from the queue.
/// </summary>
public class ChatQueueProcessor(
    ILogger<ChatQueueProcessor> _logger,
    ChatInterceptor _chatInterceptor,
    SmsService _smsService,
    IQuotaAlertService _quotaAlert)
{
    [Function(nameof(ChatQueueProcessor))]
    public async Task Run([ServiceBusTrigger(ChatQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ChatQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize chat queue message: {body}");

        _logger.LogInformation("Processing message from queue: {Message}", message.Content);

        try
        {
            var reply = await _chatInterceptor.HandleMessage(
                message.Content,
                message.UserPhoneNumber,
                message.AgentPhoneNumber,
                message.Media);

            if (reply != null)
                await _smsService.Send(message.UserPhoneNumber, reply);
        }
        catch (Exception ex)
        {
            // An exhausted balance is silent otherwise: the reply never arrives and the message
            // dead-letters. Tell the owner, then rethrow so retry and dead-lettering behave
            // exactly as before.
            await _quotaAlert.NotifyIfCreditExhaustedAsync(ex, message.AgentPhoneNumber, nameof(ChatQueueProcessor));
            throw;
        }

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
