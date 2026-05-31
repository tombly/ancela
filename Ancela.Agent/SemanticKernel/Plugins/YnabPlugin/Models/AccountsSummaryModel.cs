namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;

public class AccountsSummaryModel
{
    /// <summary>Total of all account balances; assets minus liabilities (YNAB stores debts as negative).</summary>
    public decimal NetWorth { get; set; }

    /// <summary>Sum of the accounts with a positive balance.</summary>
    public decimal TotalAssets { get; set; }

    /// <summary>Sum of the accounts with a negative balance, kept negative so NetWorth = TotalAssets + TotalLiabilities.</summary>
    public decimal TotalLiabilities { get; set; }

    public required AccountModel[] Accounts { get; set; }
}
