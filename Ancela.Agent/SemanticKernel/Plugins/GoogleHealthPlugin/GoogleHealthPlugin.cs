using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.GoogleHealthPlugin;

/// <summary>
/// A Semantic Kernel plugin exposing the owner's health data (activity, sleep, resting heart rate)
/// from the Google Health API. Responses are mapped to slim models to keep token cost down. These
/// functions read owner-private health data and are gated owner-only in <c>KernelProfilePolicy</c>.
/// </summary>
public class GoogleHealthPlugin(GoogleHealthClient _googleHealthClient)
{
    [KernelFunction("get_daily_activity")]
    [Description("Gets step count, distance, calories burned, and active minutes (light/moderate/vigorous) for a date. Defaults to today.")]
    public async Task<DailyActivityModel> GetDailyActivityAsync(
        [Description("Date in YYYY-MM-DD format, or 'today'. Defaults to today.")] string? date = null)
    {
        return await _googleHealthClient.GetDailyActivityAsync(date ?? "today");
    }

    [KernelFunction("get_sleep_summary")]
    [Description("Gets sleep duration, efficiency, and stage breakdown (deep/light/rem/awake) for a date. Defaults to today.")]
    public async Task<SleepSummaryModel> GetSleepSummaryAsync(
        [Description("Date in YYYY-MM-DD format, or 'today'. Defaults to today.")] string? date = null)
    {
        return await _googleHealthClient.GetSleepSummaryAsync(date ?? "today");
    }

    [KernelFunction("get_resting_heart_rate")]
    [Description("Gets daily resting heart rate over a date range (max 14 days).")]
    public async Task<HeartRateModel[]> GetRestingHeartRateAsync(
        [Description("Start date in YYYY-MM-DD format.")] string startDate,
        [Description("End date in YYYY-MM-DD format (within 14 days of the start).")] string endDate)
    {
        return await _googleHealthClient.GetHeartRateAsync(startDate, endDate);
    }
}
