using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Responses;

// The .NET SDKs mark their Responses API types for evaluation (OPENAI001); OpenAI's API itself is
// not. SKEXP0001: Semantic Kernel's IChatClient adapter.
#pragma warning disable OPENAI001, SKEXP0001

namespace Ancela.Agent.Services;

/// <summary>
/// The conversational model, on OpenAI's Responses API. Semantic Kernel's own OpenAI connector uses
/// /v1/chat/completions, where OpenAI rejects function tools with reasoning on, so Ancela ran
/// gpt-6-luna with reasoning off; the Responses API allows both. Semantic Kernel still calls the
/// tools, through the kernel, so the plugins and the filters (audit, guard, empty results) are
/// unchanged. V2 step 3 measured it on the live tests against chat completions: the same passes,
/// replies a little faster, the same cost (plans/V2.md, "Spike results").
/// </summary>
public static class ChatModel
{
    /// <summary>Also named in AppHost.cs (<c>AddModel</c>); change both together.</summary>
    public const string Name = "gpt-6-luna";

    /// <summary>
    /// How hard the model thinks: not at all, the same as before the move. V2 step 3 measured the
    /// alternatives (plans/V2.md, "Spike results"): on the 38 live tests every effort passed, low
    /// and medium a little slower; on the DMARC task with real 152-character Graph ids, none
    /// trashed every report in 15 of 20 runs and medium in 17, its runs twice as long. None gave
    /// up nothing measurable, so it stays; raising it is this one value.
    /// </summary>
    public const ReasoningEffort Effort = ReasoningEffort.None;

    /// <param name="effort">For live tests trying another (ANCELA_TEST_REASONING).</param>
    public static IChatCompletionService Create(OpenAIClient client, ReasoningEffort effort = Effort)
    {
        // Outermost first: Semantic Kernel calls the tools, and every request it makes to the
        // model gets RequestOptions.
        var chatClient = new ChatClientBuilder(client.GetResponsesClient().AsIChatClient(Name))
            .UseKernelFunctionInvocation()
            .Use(inner => new ResponsesOptions(inner, effort))
            .Build();

        // A tool that throws reaches the model as "function failed" unless this is on, and the
        // guard's denial says what to do instead (show the call and ask the user). Without it, in
        // the spike, a held reply went to "Bob" rather than his address. Semantic Kernel's
        // invoking client is internal; it is a FunctionInvokingChatClient.
        chatClient.GetService<FunctionInvokingChatClient>()!.IncludeDetailedErrors = true;

        return chatClient.AsChatCompletionService();
    }

    /// <summary>
    /// What every request sets: the reasoning effort; store: false, since the Responses API
    /// otherwise keeps each response at OpenAI; and so the reasoning, which a stored response
    /// would carry, comes back encrypted to pass between the tool calls of one reply.
    /// </summary>
    public static ChatOptions RequestOptions(ChatOptions? options, ReasoningEffort effort)
    {
        var with = options?.Clone() ?? new ChatOptions();
        with.Reasoning = new ReasoningOptions { Effort = effort };
        with.RawRepresentationFactory = _ => new CreateResponseOptions
        {
            StoredOutputEnabled = false,
            IncludedProperties = { IncludedResponseProperty.ReasoningEncryptedContent },
        };
        return with;
    }

    private sealed class ResponsesOptions(IChatClient inner, ReasoningEffort effort) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, RequestOptions(options, effort), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, RequestOptions(options, effort), cancellationToken);
    }
}
