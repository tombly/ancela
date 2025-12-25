using System.ComponentModel;
using Ancilla.FunctionApp.Services;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class GraphPlugin(IGraphService graphService)
{
    [KernelFunction("get_calendar_events")]
    [Description("Retrieves calendar events for the current agent")]
    public async Task<EventEntry[]> GetCalendarEventsAsync(DateTimeOffset start, DateTimeOffset end)
    {
        return await graphService.GetUserEventsAsync(start, end);
    }
}