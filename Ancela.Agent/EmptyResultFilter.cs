using System.Collections;
using Microsoft.SemanticKernel;

namespace Ancela.Agent;

/// <summary>
/// Makes a tool call that worked but found nothing say so. A failure already reaches the model
/// as "Error: …" (Semantic Kernel turns a tool's exception into that), but a null, a blank string
/// or an empty list reaches it as "" or "[]", which it has read as a failure: on 2026-10-20 a sleep
/// lookup that worked, for a night with no sleep recorded, was reported to the owner as "Google
/// Health still appears disconnected".
///
/// That only holds if tools keep their side of it: a failure throws, and is never returned as an
/// empty result, and a result that found nothing is empty rather than a record of nulls or zeros.
/// Registered after the other filters, so it runs innermost and AuditFilter records what the model
/// is shown.
/// </summary>
public class EmptyResultFilter : IFunctionInvocationFilter
{
    public const string NothingFoundMarker = "found nothing for this request";

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        await next(context);

        // A function with no return value (send_sms, save_todo) has nothing to find.
        if (!ReturnsValue(context.Function))
            return;

        if (IsEmpty(context.Result.GetValue<object>()))
            context.Result = new FunctionResult(context.Result, NothingFound(context.Function.Name));
    }

    public static string NothingFound(string functionName) =>
        $"No data: {functionName} worked and {NothingFoundMarker}. This is not an error or a " +
        "connection problem, so say there is nothing to report, not that something failed.";

    private static bool ReturnsValue(KernelFunction function)
    {
        var type = function.Metadata.ReturnParameter.ParameterType;
        return type is not null && type != typeof(void) && type != typeof(Task) && type != typeof(ValueTask);
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        ICollection collection => collection.Count == 0,
        IEnumerable sequence => !sequence.GetEnumerator().MoveNext(),
        _ => false,
    };
}
