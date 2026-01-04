using System.Net;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Twilio.Security;

namespace Ancela.FunctionApp;

/// <summary>
/// Handles incoming SMS messages via Twilio web hook.
/// </summary>
public class SmsFunction(ILogger<SmsFunction> _logger, QueueServiceClient _queueServiceClient)
{
    [Function("IncomingSms")]
    public async Task<HttpResponseData> IncomingSms([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request)
    {
        try
        {
            _logger.LogInformation("IncomingSms triggered");

            var bodyString = await request.ReadAsStringAsync();

            if (!ValidateRequest(request, bodyString))
            {
                _logger.LogWarning("Invalid Twilio request signature");
                return request.CreateResponse(HttpStatusCode.Forbidden);
            }

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

            // Extract media URLs if present.
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
            _logger.LogError(exception, "Error processing incoming SMS");
            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Error: {exception.Message}");
            return response;
        }
    }

    private static bool ValidateRequest(HttpRequestData request, string? bodyString)
    {
        var authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? throw new Exception("TWILIO_AUTH_TOKEN not set");

        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(bodyString))
        {
            var formValues = System.Web.HttpUtility.ParseQueryString(bodyString);
            parameters = formValues.AllKeys.ToDictionary(k => k!, k => formValues[k]!);
        }

        // Build the actual request URL using forwarded headers when behind a proxy
        var requestUrl = GetActualRequestUrl(request);
        var requestValidator = new RequestValidator(authToken);
        var signature = request.Headers.TryGetValues("X-Twilio-Signature", out var sigValues)
            ? sigValues.FirstOrDefault()
            : null;

        return requestValidator.Validate(requestUrl, parameters, signature);
    }

    /// <summary>
    /// Constructs the actual request URL considering possible proxy headers.
    /// This is necessary when the function app is running inside a
    /// container.
    /// </summary>
    private static string GetActualRequestUrl(HttpRequestData request)
    {
        // Check various proxy headers that Azure Functions might use.
        string? host = null;
        string? proto = null;

        // Try X-Forwarded-Host first.
        if (request.Headers.TryGetValues("X-Forwarded-Host", out var forwardedHosts))
            host = forwardedHosts?.FirstOrDefault();

        // Try X-Original-Host if X-Forwarded-Host not present.
        if (string.IsNullOrWhiteSpace(host) && request.Headers.TryGetValues("X-Original-Host", out var originalHosts))
            host = originalHosts?.FirstOrDefault();

        // Try Host header as fallback.
        if (string.IsNullOrWhiteSpace(host) && request.Headers.TryGetValues("Host", out var hosts))
            host = hosts?.FirstOrDefault();

        // Try X-Forwarded-Proto first.
        if (request.Headers.TryGetValues("X-Forwarded-Proto", out var forwardedProtos))
            proto = forwardedProtos?.FirstOrDefault();

        // Try X-ARR-SSL if X-Forwarded-Proto not present (Azure specific).
        if (string.IsNullOrWhiteSpace(proto) && request.Headers.TryGetValues("X-ARR-SSL", out var arrSsl))
            proto = arrSsl?.FirstOrDefault() != null ? "https" : "http";

        // Default to https if host is set but proto isn't.
        if (!string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(proto))
            proto = "https";

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(proto))
        {
            var path = request.Url.PathAndQuery;
            return $"{proto}://{host}{path}";
        }

        // Fall back to the request URL if no forwarded headers are present.
        return request.Url.ToString();
    }
}