using System.Text.Json.Serialization;

namespace Ancela.Agent.Services;

/// <summary>
/// One message on the chat-messages queue. Everything that lets the user talk to Ancela puts
/// its messages here, so they're handled one at a time in the order they arrived. The queue's
/// senders are trusted: <see cref="UserPhoneNumber"/> is taken at face value. There are two, each
/// with its own proof of who is sending: the Twilio webhook (signature-checked) and the owner's
/// own Azure sign-in (`ancela chat`, with the Data Sender role on this queue).
/// </summary>
public record ChatQueueMessage
{
    public const string QueueName = "chat-messages";

    /// <summary>
    /// The message's correlation ID for everything done to answer it, so `ancela chat` can follow
    /// its own message through the audit log. Messages queued before this existed get a new one.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Content { get; init; } = string.Empty;
    public string UserPhoneNumber { get; init; } = string.Empty;
    public string AgentPhoneNumber { get; init; } = string.Empty;
    public Media[] Media { get; init; } = [];

    /// <summary>Where the reply goes. Messages queued before this existed came by SMS.</summary>
    public ChatChannel Channel { get; init; } = ChatChannel.Sms;
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatChannel>))]
public enum ChatChannel
{
    /// <summary>A text through Twilio; the reply goes back as one.</summary>
    Sms,

    /// <summary>
    /// `ancela chat` on the owner's computer, for when SMS can't reach them (on a plane, out of
    /// signal). The reply is recorded in the audit log (<see cref="OutboundLog"/>) instead of
    /// texted, where the CLI reads it: a text would reach the phone later as a duplicate.
    /// </summary>
    Cli,
}
