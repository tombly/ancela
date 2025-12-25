using System.ComponentModel;
using Ancilla.FunctionApp.Services;
using Microsoft.SemanticKernel;

namespace Ancilla.FunctionApp;

public class GraphPlugin(IGraphService graphService)
{
    [KernelFunction("get_calendar_events")]
    [Description("Retrieves calendar events for the user within the specified date range.")]
    public async Task<EventEntry[]> GetCalendarEventsAsync(
         [Description("The start date and time of the range to query")] 
         DateTimeOffset start, 
         [Description("The end date and time of the range to query")]
         DateTimeOffset end)
    {
        return await graphService.GetUserEventsAsync(start, end);
    }
}