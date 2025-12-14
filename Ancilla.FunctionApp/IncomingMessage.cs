using System.Net;
using Ancilla.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Ancilla.FunctionApp;

public class MessageFunction(ILogger<MessageFunction> _logger, ChatInterceptor _chatInterceptor)
{
    [Function("IncomingMessage")]
    public async Task<HttpResponseData> IncomingMessage([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
    {
        try
        {
            _logger.LogInformation("IncomingMessage triggered");

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

            var allowedPhoneNumbers = Environment.GetEnvironmentVariable("ALLOWED_PHONE_NUMBERS") ?? throw new Exception("ALLOWED_PHONE_NUMBERS not set");
            if (!allowedPhoneNumbers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(from))
            {
                var forbiddenResponse = request.CreateResponse(HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("Phone number not allowed.");
                return forbiddenResponse;
            }

            var reply = await _chatInterceptor.HandleMessage(body, from, to);

            var response = request.CreateResponse(HttpStatusCode.OK);
            if (reply != null)
                await response.WriteStringAsync(reply);
            return response;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error processing incoming message");
            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Error: {exception.Message}");
            return response;
        }
    }
}