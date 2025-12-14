using System.Net;
using Ancilla.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Twilio.Security;

namespace Ancilla.FunctionApp;

public class SmsFunction(ILogger<SmsFunction> _logger, ChatInterceptor _chatInterceptor, SmsService _smsService)
{
    [Function("IncomingSms")]
    public async Task<HttpResponseData> IncomingSms([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
    {
        try
        {
            _logger.LogInformation("IncomingSms triggered");

            if (!await ValidateRequest(request))
                return request.CreateResponse(HttpStatusCode.Forbidden);

            var bodyString = await request.ReadAsStringAsync();
            var formValues = System.Web.HttpUtility.ParseQueryString(bodyString ?? string.Empty);
            var body = formValues["Body"];
            var userPhoneNumber = formValues["From"];
            var agentPhoneNumber = formValues["To"];

            if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(userPhoneNumber) || string.IsNullOrWhiteSpace(agentPhoneNumber))
            {
                var badResponse = request.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing required parameters.");
                return badResponse;
            }

            var reply = await _chatInterceptor.HandleMessage(body, userPhoneNumber, agentPhoneNumber);
            if (reply != null)
                await _smsService.Send(userPhoneNumber, reply);

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

    private static async Task<bool> ValidateRequest(HttpRequestData request)
    {
        var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? throw new Exception("TWILIO_AUTH_TOKEN not set");

        Dictionary<string, string>? parameters = null;
        var bodyString = await request.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(bodyString))
        {
            var formValues = System.Web.HttpUtility.ParseQueryString(bodyString);
            parameters = formValues.AllKeys.ToDictionary(k => k!, k => formValues[k]!);
        }

        var requestUrl = request.Url.ToString();
        var requestValidator = new RequestValidator(authToken);
        var signature = request.Headers.TryGetValues("X-Twilio-Signature", out var sigValues)
            ? sigValues.FirstOrDefault()
            : null;
        var isValid = requestValidator.Validate(requestUrl, parameters, signature);
        return isValid;
    }
}