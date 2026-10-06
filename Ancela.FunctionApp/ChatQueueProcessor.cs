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
    OwnerService _ownerService)
{
    [Function(nameof(ChatQueueProcessor))]
    public async Task Run([ServiceBusTrigger(ChatQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ChatQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize chat queue message: {body}");

        _logger.LogInformation("Processing message from queue: {Message}", message.Content);

        string? reply;
        try
        {
            reply = await _chatInterceptor.HandleMessage(
                message.Content,
                message.UserPhoneNumber,
                message.AgentPhoneNumber,
                message.Media);
        }
        catch (ModelOutOfFundsException)
        {
            // Answer rather than fail. Without this the message would be retried ten times and
            // dead-lettered, which is how an empty balance went unexplained for nine days in
            // September: the sender saw nothing at all. Retrying cannot help — the balance is
            // empty until it is paid — so the message is completed, not abandoned.
            await _smsService.Send(
                message.UserPhoneNumber,
                OutOfFundsMessages.ForUser(_ownerService.IsOwner(message.UserPhoneNumber)));
            _logger.LogWarning("Out of OpenAI credit; answered {User} instead of retrying.", message.UserPhoneNumber);
            return;
        }

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
