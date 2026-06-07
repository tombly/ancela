using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.FitbitPlugin;

public class FitbitPlugin(FitbitClient _fitbitClient)
{
    [KernelFunction("get_daily_activity")]
    [Description("Gets daily activity summary including steps, distance, calories burned, and active minutes. Defaults to today.")]
    public async Task<DailyActivityModel> GetDailyActivityAsync(
        [Description("Date in YYYY-MM-DD format, or 'today'. Defaults to today.")] string? date = null)
    {
        return await _fitbitClient.GetDailyActivityAsync(date ?? "today");
    }

    [KernelFunction("get_sleep_summary")]
    [Description("Gets sleep summary including time asleep, time in bed, efficiency, and sleep stage breakdown. Defaults to today.")]
    public async Task<SleepSummaryModel> GetSleepSummaryAsync(
        [Description("Date in YYYY-MM-DD format, or 'today'. Defaults to today.")] string? date = null)
    {
        return await _fitbitClient.GetSleepSummaryAsync(date ?? "today");
    }

    [KernelFunction("get_resting_heart_rate")]
    [Description("Gets resting heart rate and heart rate zone data for a date range.")]
    public async Task<HeartRateModel[]> GetRestingHeartRateAsync(
        [Description("Start date in YYYY-MM-DD format.")] string startDate,
        [Description("End date in YYYY-MM-DD format.")] string endDate)
    {
        return await _fitbitClient.GetHeartRateAsync(startDate, endDate);
    }
}
