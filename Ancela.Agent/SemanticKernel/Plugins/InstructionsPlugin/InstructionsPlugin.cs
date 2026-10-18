using System.ComponentModel;
using Ancela.Agent.Services;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.InstructionsPlugin;

/// <summary>
/// Per-user instructions: how a user wants Ancela to behave in every conversation with them (a
/// persona, what to call them, a lasting preference). They are saved on the user's profile and
/// listed in the Chat prompt's Current context on every message, so unlike knowledge they apply
/// without being looked up. Chat only: neither function is in an autonomous allow-list, and
/// <see cref="AutonomousToolGuardFilter"/> switches both off once a message has read email,
/// calendar or the web, so text someone else wrote can't become a lasting instruction.
/// </summary>
public class InstructionsPlugin(IUserService _userService)
{
    // Every message carries all of them, so the caps bound what they add to each prompt.
    public const int MaxInstructions = 10;
    public const int MaxLength = 300;

    [KernelFunction("remember_instruction")]
    [Description("Saves an instruction the user wants you to follow in every conversation with them from now on: a persona or tone (\"talk like HAL 9000\"), what to call them, or a lasting preference (\"use metric units\"). Only when the user asks you to behave a certain way from now on; facts go in save_knowledge and tasks in save_todo. Up to 10 per user, 300 characters each.")]
    public async Task<string> RememberInstructionAsync(Kernel kernel,
        [Description("The instruction, in the user's own words where possible.")] string text)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        // One line: the text is listed as a bullet in a system message, where a line break would
        // let it start a bullet of its own that reads like one of Ancela's rules.
        var normalized = string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            throw new ArgumentException("text is required", nameof(text));
        if (normalized.Length > MaxLength)
            throw new ArgumentException(
                $"An instruction can be at most {MaxLength} characters; this one is {normalized.Length}. Shorten it and try again.",
                nameof(text));

        var profile = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber)
            ?? throw new InvalidOperationException("The current user has no profile.");

        // The model sometimes repeats a call; saving the same text twice would only crowd the list.
        var existing = profile.Instructions.FirstOrDefault(i => string.Equals(i.Text, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return $"Already saved as instruction {existing.Id}.";

        if (profile.Instructions.Count >= MaxInstructions)
            throw new InvalidOperationException(
                $"The user already has {MaxInstructions} instructions, the most allowed. Ask which one to forget first.");

        var instruction = new UserInstruction
        {
            Id = Guid.NewGuid(),
            Text = normalized,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _userService.SetInstructionsAsync(agentPhoneNumber, userPhoneNumber, [.. profile.Instructions, instruction]);
        return $"Saved instruction {instruction.Id}: {instruction.Text}";
    }

    [KernelFunction("forget_instruction")]
    [Description("Removes one of the user's saved instructions, by the id listed in Current context. Use when the user asks you to stop doing something they told you to do from now on.")]
    public async Task<string> ForgetInstructionAsync(Kernel kernel,
        [Description("The instruction's id (GUID), as listed in Current context.")] string instructionId)
    {
        var (agentPhoneNumber, userPhoneNumber) = RequireContext(kernel);

        if (!Guid.TryParse(instructionId, out var id))
            throw new ArgumentException($"instructionId must be a GUID; got '{instructionId}'", nameof(instructionId));

        var profile = await _userService.GetAsync(agentPhoneNumber, userPhoneNumber)
            ?? throw new InvalidOperationException("The current user has no profile.");

        var remaining = profile.Instructions.Where(i => i.Id != id).ToList();
        if (remaining.Count == profile.Instructions.Count)
            throw new ArgumentException($"The user has no instruction {id}.", nameof(instructionId));

        await _userService.SetInstructionsAsync(agentPhoneNumber, userPhoneNumber, remaining);
        return $"Forgot instruction {id}.";
    }

    private static (string agentPhoneNumber, string userPhoneNumber) RequireContext(Kernel kernel)
    {
        var agentPhoneNumber = kernel.Data["agentPhoneNumber"]?.ToString();
        var userPhoneNumber = kernel.Data["userPhoneNumber"]?.ToString();

        if (string.IsNullOrWhiteSpace(agentPhoneNumber))
            throw new InvalidOperationException("agentPhoneNumber is required in kernel data");
        if (string.IsNullOrWhiteSpace(userPhoneNumber))
            throw new InvalidOperationException("userPhoneNumber is required in kernel data");

        return (agentPhoneNumber, userPhoneNumber);
    }
}
