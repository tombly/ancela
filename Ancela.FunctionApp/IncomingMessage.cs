using System.Net;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Ancela.FunctionApp;

/// <summary>
/// Handles incoming messages via HTTP trigger. Used for development and testing.
/// </summary>
public class MessageFunction(ILogger<MessageFunction> _logger, QueueServiceClient _queueServiceClient)
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

            if (string.IsNullOrWhiteSpace(userPhoneNumber) || string.IsNullOrWhiteSpace(agentPhoneNumber))
            {
                var badResponse = request.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing required parameters.");
                return badResponse;
            }

            // Extract media URLs if present
            var mediaUrls = new List<string>();
            if (int.TryParse(formValues["NumMedia"], out var numMedia) && numMedia > 0)
            {
                for (int i = 0; i < numMedia; i++)
                {
                    var mediaUrl = formValues[$"MediaUrl{i}"];
                    if (!string.IsNullOrWhiteSpace(mediaUrl))
                        mediaUrls.Add(mediaUrl);
                }
            }

           var queueMessage = new ChatQueueMessage {
                Content = body ?? string.Empty,
                UserPhoneNumber = userPhoneNumber,
                AgentPhoneNumber = agentPhoneNumber,
                MediaUrls = [.. mediaUrls]
           };

            var queueClient = _queueServiceClient.GetQueueClient(ChatQueueMessage.QueueName);
            await queueClient.CreateIfNotExistsAsync();
            await queueClient.SendMessageAsync(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(queueMessage)));

            return request.CreateResponse(HttpStatusCode.OK);
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