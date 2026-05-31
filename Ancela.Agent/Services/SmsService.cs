using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Ancela.Agent.Services;

/// <summary>
/// A DI-friendly wrapper around <see cref="TwilioClient"/>.
/// </summary>
public class SmsService
{
    // Twilio rejects message bodies longer than 1600 characters, so cap the body
    // and mark it when content is dropped rather than letting the send throw.
    private const int MaxSmsLength = 1600;
    private const string TruncationSuffix = "... (truncated)";

    private readonly string _twilioPhoneNumber;

    public SmsService()
    {
        _twilioPhoneNumber = Environment.GetEnvironmentVariable("TWILIO_PHONE_NUMBER") ?? throw new Exception("TWILIO_PHONE_NUMBER not set");
        var twilioAccountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? throw new Exception("TWILIO_ACCOUNT_SID not set");
        var twilioAuthToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? throw new Exception("TWILIO_AUTH_TOKEN not set");

        TwilioClient.Init(twilioAccountSid, twilioAuthToken);
    }

    public virtual async Task Send(string phoneNumbers, string message)
    {
        var body = Truncate(message);

        foreach (var phoneNumber in phoneNumbers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            await MessageResource.CreateAsync(
                from: new PhoneNumber(_twilioPhoneNumber),
                to: new PhoneNumber(phoneNumber),
                body: body);
        }
    }

    private static string Truncate(string message) =>
        message.Length <= MaxSmsLength
            ? message
            : message[..(MaxSmsLength - TruncationSuffix.Length)].TrimEnd() + TruncationSuffix;
}
