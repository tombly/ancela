using Ynab.Api.Client;

namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;

public class AccountModel
{
    public required string Name { get; set; }
    public AccountType Type { get; set; }
    public bool OnBudget { get; set; }
    public string? Note { get; set; }
    public decimal Balance { get; set; }
    public decimal ClearedBalance { get; set; }
    public decimal UnclearedBalance { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
}
