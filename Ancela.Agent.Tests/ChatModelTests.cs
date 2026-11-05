using Ancela.Agent.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001

namespace Ancela.Agent.Tests;

/// <summary>What every request to the model carries on the Responses API (ChatModel).</summary>
public class ChatModelTests
{
    [Fact]
    public void ARequest_CarriesTheEffort() =>
        ChatModel.RequestOptions(new ChatOptions(), ReasoningEffort.Medium).Reasoning!.Effort.Should().Be(ReasoningEffort.Medium);

    [Fact]
    public void NothingIsStoredAtOpenAI_AndTheReasoningComesBackEncrypted()
    {
        var sent = ChatModel.RequestOptions(new ChatOptions(), ChatModel.Effort);

        var raw = sent.RawRepresentationFactory!(null!).Should().BeOfType<CreateResponseOptions>().Subject;
        raw.StoredOutputEnabled.Should().BeFalse(because: "the Responses API otherwise keeps every response at OpenAI");
        raw.IncludedProperties.Should().Contain(IncludedResponseProperty.ReasoningEncryptedContent);
    }

    [Fact]
    public void TheOptionsSemanticKernelPasses_AreLeftAsTheyWere()
    {
        // Semantic Kernel sends the same options on each tool call of one reply.
        var options = new ChatOptions { ModelId = "kept" };

        ChatModel.RequestOptions(options, ReasoningEffort.Medium);

        options.Reasoning.Should().BeNull();
        options.RawRepresentationFactory.Should().BeNull();
    }
}
