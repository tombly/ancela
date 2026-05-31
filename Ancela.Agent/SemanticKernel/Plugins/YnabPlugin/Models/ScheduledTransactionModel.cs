using Ynab.Api.Client;

namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;

public class ScheduledTransactionModel
{
    /// <summary>The next date this transaction is scheduled to occur.</summary>
    public DateTimeOffset DateNext { get; set; }

    public ScheduledTransactionFrequency Frequency { get; set; }

    /// <summary>Negative for outflows (spending), positive for inflows (income).</summary>
    public decimal Amount { get; set; }

    public string? PayeeName { get; set; }
    public string? CategoryName { get; set; }
    public required string AccountName { get; set; }
    public string? Memo { get; set; }
}
