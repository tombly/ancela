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
    OwnerService _ownerService,
    OutboundLog _outbound,
    CorrelationContext _correlation)
{
    [Function(nameof(ChatQueueProcessor))]
    public async Task Run([ServiceBusTrigger(ChatQueueMessage.QueueName, Connection = "servicebus")] string body)
    {
        var message = JsonSerializer.Deserialize<ChatQueueMessage>(body)
            ?? throw new InvalidOperationException($"Failed to deserialize chat queue message: {body}");

        _logger.LogInformation("Processing message from queue: {Message}", message.Content);
        _correlation.Use(message.Id);

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
            await ReplyAsync(message, OutOfFundsMessages.ForUser(_ownerService.IsOwner(message.UserPhoneNumber)));
            _logger.LogWarning("Out of OpenAI credit; answered {User} instead of retrying.", message.UserPhoneNumber);
            return;
        }

        await ReplyAsync(message, reply);
        _logger.LogInformation("Successfully processed message from queue");
    }

    private async Task ReplyAsync(ChatQueueMessage message, string? reply)
    {
        // A CLI message's reply goes in the audit log, where `ancela chat` reads it by the
        // message's ID, and not by text: the CLI is for when SMS can't reach the phone, and the
        // text would arrive later as a duplicate. With no reply (an unregistered sender), the
        // empty record still tells the CLI to stop waiting.
        if (message.Channel == ChatChannel.Cli)
            await _outbound.RecordAsync(message.AgentPhoneNumber, message.UserPhoneNumber, "cli", "reply", reply ?? string.Empty);
        else if (reply != null)
            await _smsService.Send(message.UserPhoneNumber, reply);
    }
}
