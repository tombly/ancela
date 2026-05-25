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
        var result = await response.Content.ReadFromJsonAsync<TavilySearchResponse>();
        return result?.Results?.Select(r => new SearchResult
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
        var result = await response.Content.ReadFromJsonAsync<TavilyExtractResponse>();
        return result?.Results?.FirstOrDefault()?.RawContent ?? string.Empty;
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
    }

    private class TavilyExtractResult
    {
        [JsonPropertyName("raw_content")]
        public string RawContent { get; set; } = string.Empty;
    }
}
