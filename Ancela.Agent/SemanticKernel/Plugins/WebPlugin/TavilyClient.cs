using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ancela.Agent.SemanticKernel.Plugins.WebPlugin.Models;

namespace Ancela.Agent.SemanticKernel.Plugins.WebPlugin;

public interface ITavilyClient
{
    Task<SearchResult[]> SearchAsync(string query, int maxResults = 5, int? recencyDays = null);
    Task<string> FetchAsync(string url);
}

public class TavilyClient(IHttpClientFactory _httpClientFactory) : ITavilyClient
{
    public async Task<SearchResult[]> SearchAsync(string query, int maxResults = 5, int? recencyDays = null)
    {
        var client = _httpClientFactory.CreateClient("tavily");
        var payload = new { query, max_results = maxResults, search_depth = "basic", days = recencyDays };
        var response = await client.PostAsJsonAsync("/search", payload);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TavilySearchResponse>()
            ?? throw new InvalidOperationException("Tavily search returned an empty body.");
        return result.Results?.Select(r => new SearchResult
        {
            Url = r.Url,
            Title = r.Title,
            Content = r.Content,
            Score = r.Score,
            PublishedDate = r.PublishedDate
        }).ToArray() ?? [];
    }

    public async Task<string> FetchAsync(string url)
    {
        var client = _httpClientFactory.CreateClient("tavily");
        var payload = new { urls = new[] { url } };
        var response = await client.PostAsJsonAsync("/extract", payload);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TavilyExtractResponse>()
            ?? throw new InvalidOperationException("Tavily extract returned an empty body.");

        // A page Tavily can't read still comes back 200, with the reason under failed_results and
        // no result. Returning "" for it made a failed fetch look like a page with no text.
        var page = result.Results?.FirstOrDefault();
        if (page is null)
        {
            var reason = result.FailedResults?.FirstOrDefault()?.Error;
            throw new HttpRequestException($"Could not fetch that page: {reason ?? "Tavily returned no result for it"}.");
        }

        // A page that loaded but has no readable text (one that needs JavaScript, say) is "".
        return page.RawContent;
    }

    private class TavilySearchResponse
    {
        [JsonPropertyName("results")]
        public TavilySearchResult[]? Results { get; set; }
    }

    private class TavilySearchResult
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
        [JsonPropertyName("score")]
        public double Score { get; set; }
        [JsonPropertyName("published_date")]
        public string? PublishedDate { get; set; }
    }

    private class TavilyExtractResponse
    {
        [JsonPropertyName("results")]
        public TavilyExtractResult[]? Results { get; set; }
        [JsonPropertyName("failed_results")]
        public TavilyFailedResult[]? FailedResults { get; set; }
    }

    private class TavilyFailedResult
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private class TavilyExtractResult
    {
        [JsonPropertyName("raw_content")]
        public string RawContent { get; set; } = string.Empty;
    }
}
