namespace Ancela.Agent.SemanticKernel.Plugins.WebPlugin.Models;

public class SearchResult
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public string? PublishedDate { get; set; }
}
