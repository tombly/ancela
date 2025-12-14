using System.Net;
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
            var userPhoneNumber = formValues["From"];
            var agentPhoneNumber = formValues["To"];

            if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(userPhoneNumber) || string.IsNullOrWhiteSpace(agentPhoneNumber))
            {
                var badResponse = request.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing required parameters.");
                return badResponse;
            }

            var reply = await _chatInterceptor.HandleMessage(body, userPhoneNumber, agentPhoneNumber);

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