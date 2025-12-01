using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Ancilla.FunctionApp;

/// <summary>
/// A DI-friendly wrapper around <see cref="TwilioClient"/>.
/// </summary>
public class SmsService
{
    private readonly string _twilioPhoneNumber;

    public SmsService()
    {
        _twilioPhoneNumber = Environment.GetEnvironmentVariable("TWILIO_PHONE_NUMBER") ?? throw new Exception("TWILIO_PHONE_NUMBER not set");
        var twilioAccountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? throw new Exception("TWILIO_ACCOUNT_SID not set");
        var twilioAuthToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? throw new Exception("TWILIO_AUTH_TOKEN not set");

        TwilioClient.Init(twilioAccountSid, twilioAuthToken);
    }

    public async Task Send(string phoneNumbers, string message)
    {
        if (message.Length > 160)
            message = string.Concat(message.AsSpan(0, 157), "...");

        foreach (var phoneNumber in phoneNumbers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            await MessageResource.CreateAsync(
                from: new PhoneNumber(_twilioPhoneNumber),
                to: new PhoneNumber(phoneNumber),
                body: message);
        }
    }
}