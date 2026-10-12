using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.WeatherPlugin;

public class WeatherPlugin(IWeatherClient _weatherClient)
{
    [KernelFunction("get_forecast")]
    [Description("Gets the weather forecast for a US location from the National Weather Service: "
        + "today's daytime high, tonight's low, conditions, and the next few periods. Prefer this "
        + "over web_search for any weather question — it labels highs and lows explicitly, which "
        + "search snippets do not.")]
    public async Task<WeatherForecast> GetForecastAsync(
        [Description("US city and state, e.g. 'Mill Creek, WA'. Use the user's home location unless they name somewhere else. Include the state — a bare city name is ambiguous.")] string location)
    {
        return await _weatherClient.GetForecastAsync(location);
    }
}
