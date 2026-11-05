using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ancela.Agent.Services;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;

namespace Ancela.Agent.Tests;

/// <summary>
/// What a live model test run spends, and the model it runs on. The live tests build the model as
/// production does (<see cref="ChatModel"/>); two environment variables change that:
///   ANCELA_TEST_REASONING   none | low | medium, to try another effort; unset, production's
///   ANCELA_TEST_USAGE_LOG   a JSON-lines file for each request's tokens and time, each reply's time
///                           each tool call's name and outcome, and each check's attempts; unset,
///                           nothing is recorded
/// Records carry no message text: test name, counts and milliseconds only. V2 step 3 measured the
/// Responses API with it (plans/V2.md, "Spike results"); summarizing a log is left to a script.
/// </summary>
public static class LiveTestUsage
{
    /// <summary>ANCELA_TEST_REASONING, or "production": ChatModel.Effort.</summary>
    public static string Effort => Environment.GetEnvironmentVariable("ANCELA_TEST_REASONING") ?? "production";
    private static string? LogPath => Environment.GetEnvironmentVariable("ANCELA_TEST_USAGE_LOG");

    /// <summary>The live test making the current request, for the records.</summary>
    public static readonly AsyncLocal<string?> CurrentTest = new();

    private static readonly Lock WriteLock = new();

    public static OpenAIClient CreateOpenAIClient(string apiKey) =>
        LogPath is null
            ? new OpenAIClient(apiKey)
            : new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(new HttpClient(new UsageRecordingHandler(new HttpClientHandler()))),
            });

    public static IChatCompletionService CreateChatService(OpenAIClient client) =>
        ChatModel.Create(client, Effort switch
        {
            "production" => ChatModel.Effort,
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            var other => throw new InvalidOperationException($"ANCELA_TEST_REASONING '{other}' is not none, low or medium."),
        });

    public static void Write(object record)
    {
        if (LogPath is not { } path)
            return;
        var line = JsonSerializer.Serialize(record);
        lock (WriteLock)
            File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>The test method on the stack: the state machine of a type deriving from AgentTestBase.</summary>
    public static string TestNameFromStack()
    {
        foreach (var frame in new StackTrace().GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            var owner = type?.DeclaringType ?? type;
            if (owner is null || owner == typeof(AgentTestBase) || !typeof(AgentTestBase).IsAssignableFrom(owner))
                continue;
            var name = type!.Name.StartsWith('<') ? type.Name[1..type.Name.IndexOf('>')] : frame.GetMethod()!.Name;
            return $"{owner.Name}.{name}";
        }
        return "unknown";
    }

    /// <summary>Records each tool call's name and whether it worked (no arguments or results).</summary>
    public sealed class ToolRecorder : Microsoft.SemanticKernel.IFunctionInvocationFilter
    {
        public async Task OnFunctionInvocationAsync(Microsoft.SemanticKernel.FunctionInvocationContext context, Func<Microsoft.SemanticKernel.FunctionInvocationContext, Task> next)
        {
            try
            {
                await next(context);
                Write(new { kind = "tool", test = CurrentTest.Value ?? "unknown", effort = Effort, function = context.Function.Name, ok = true });
            }
            catch (Exception ex)
            {
                Write(new { kind = "tool", test = CurrentTest.Value ?? "unknown", effort = Effort, function = context.Function.Name, ok = false, error = ex.GetType().Name });
                throw;
            }
        }
    }

    /// <summary>Records each OpenAI request's token counts and time, from the usage block of its response.</summary>
    private sealed class UsageRecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            clock.Stop();

            var usage = TryParse(body)?["usage"];
            Write(new
            {
                kind = "request",
                test = CurrentTest.Value ?? "unknown",
                effort = Effort,
                endpoint = request.RequestUri?.AbsolutePath,
                status = (int)response.StatusCode,
                ms = clock.ElapsedMilliseconds,
                input = (int?)(usage?["input_tokens"] ?? usage?["prompt_tokens"]),
                cached = (int?)(usage?["input_tokens_details"]?["cached_tokens"] ?? usage?["prompt_tokens_details"]?["cached_tokens"]),
                output = (int?)(usage?["output_tokens"] ?? usage?["completion_tokens"]),
                reasoning = (int?)(usage?["output_tokens_details"]?["reasoning_tokens"] ?? usage?["completion_tokens_details"]?["reasoning_tokens"]),
                // An error's code only, never its message, which can quote the request.
                error = response.IsSuccessStatusCode ? null : (string?)TryParse(body)?["error"]?["code"],
            });

            var replaced = new StringContent(body);
            foreach (var header in response.Content.Headers)
                replaced.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = replaced;
            return response;
        }

        private static JsonNode? TryParse(string body)
        {
            try { return JsonNode.Parse(body); }
            catch (JsonException) { return null; }
        }
    }
}
