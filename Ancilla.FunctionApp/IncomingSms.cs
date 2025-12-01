using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Twilio.Security;

namespace Ancilla.FunctionApp;

public class SmsFunction(ILogger<SmsFunction> _logger, ChatService _chatService)
{
    [Function("IncomingSms")]
    public async Task<HttpResponseData> IncomingSms([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
    {
        try
        {
            _logger.LogInformation("IncomingSms triggered");

            //if (!await ValidateRequest(request))
            //    return request.CreateResponse(HttpStatusCode.Forbidden);

            var bodyString = await request.ReadAsStringAsync();
            var formValues = System.Web.HttpUtility.ParseQueryString(bodyString ?? string.Empty);
            var body = formValues["Body"];
            var from = formValues["From"];
            var to = formValues["To"];

            if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                var badResponse = request.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing required parameters.");
                return badResponse;
            }

            var reply = await _chatService.Chat(body, from, to);
            await _chatService.SendReply(reply, from);

            return request.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error processing incoming SMS");
            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Error: {exception.Message}");
            return response;
        }
    }

    private static async Task<bool> ValidateRequest(HttpRequest request)
    {
        var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? throw new Exception("TWILIO_AUTH_TOKEN not set");

        Dictionary<string, string>? parameters = null;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            parameters = form.ToDictionary(p => p.Key, p => p.Value.ToString());
        }

        var requestUrl = $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
        var requestValidator = new RequestValidator(authToken);
        var signature = request.Headers["X-Twilio-Signature"];
        var isValid = requestValidator.Validate(requestUrl, parameters, signature);
        return isValid;
    }
}