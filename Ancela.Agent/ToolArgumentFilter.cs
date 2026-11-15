using System.Text.Json;
using Microsoft.SemanticKernel;

namespace Ancela.Agent;

/// <summary>
/// Hands a tool its arguments as text, as Semantic Kernel's chat-completions connector did. On
/// the Responses API (<c>ChatModel</c>) they arrive as the JSON values the model wrote, and
/// Semantic Kernel converts a JSON number or boolean but not a JSON string to an enum: from
/// 2026-11-05, when the Responses API went live, every <c>get_recent_emails</c> call that named
/// its folder failed before reaching the mailbox, and on 2026-11-15 Ancela told the owner email
/// wasn't working. Text converts to every parameter type a tool takes (an enum by name, ignoring
/// case). A JSON null is dropped, so the parameter keeps its default.
/// Registered first, so the other filters, the audit included, see the arguments the tool gets.
/// </summary>
public class ToolArgumentFilter : IFunctionInvocationFilter
{
    public Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        foreach (var (name, value) in context.Arguments.ToArray())
        {
            if (value is not JsonElement json)
                continue;
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                context.Arguments.Remove(name);
            else
                context.Arguments[name] = json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText();
        }

        return next(context);
    }
}
