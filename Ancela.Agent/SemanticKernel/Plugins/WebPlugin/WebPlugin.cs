using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.WebPlugin;

public class WebPlugin(ITavilyClient _tavilyClient)
{
    [KernelFunction("web_search")]
    [Description("Search the web for current information. Returns results with URLs, titles, and content excerpts.")]
    public async Task<SearchResult[]> SearchAsync(
        [Description("The search query.")] string query,
        [Description("Maximum number of results to return (default 5).")] int maxResults = 5,
        [Description("Limit results to the past N days (optional).")] int? recencyDays = null)
    {
        return await _tavilyClient.SearchAsync(query, maxResults, recencyDays);
    }

    [KernelFunction("web_fetch")]
    [Description("Fetch and extract the text content of a specific URL. Use when the user provides a URL or when you need the full content of a page found via web_search.")]
    public async Task<string> FetchAsync(
        [Description("The URL to fetch.")] string url)
    {
        return await _tavilyClient.FetchAsync(url);
    }
}
