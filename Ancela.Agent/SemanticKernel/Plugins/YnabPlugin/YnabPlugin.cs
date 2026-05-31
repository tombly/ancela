using System.ComponentModel;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;
using Microsoft.SemanticKernel;

namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;

/// <summary>
/// A Semantic Kernel plugin for YNAB. The YNAB API responses are mapped to
/// simpler models to (1) reduce the number of tokens that are sent to the
/// model and (2) to allow for customization of the data, such as calculating
/// the monthly need for a category, and (3) sending the API models seems to
/// cause the GetChatMessageContentAsync() method to hang in some cases.
///
/// Favors YNAB's pre-aggregated data: "how much" questions about accounts and
/// categories are answered from <see cref="GetAccountsAsync"/> /
/// <see cref="GetCategoriesAsync"/> / <see cref="GetMonthSummaryAsync"/> rather
/// than by summing transactions. <see cref="GetTransactionsAsync"/> is for
/// payee-level, listing, and status questions.
/// </summary>
public class YnabPlugin(YnabClient _ynabClient)
{
    [KernelFunction("get_accounts")]
    [Description("Gets account balances along with the computed net worth, total assets, and total liabilities")]
    public async Task<AccountsSummaryModel> GetAccountsAsync()
    {
        return await _ynabClient.GetAccountsAsync();
    }

    [KernelFunction("get_categories")]
    [Description("Gets budget categories with the amount budgeted, spent (activity), and available (balance), plus goal progress. Defaults to the current month. Use this for category spending totals instead of summing transactions.")]
    public async Task<CategoryModel[]> GetCategoriesAsync(
        [Description("Any date within the target month; omit for the current month.")] DateTimeOffset? month = null)
    {
        return await _ynabClient.GetCategoriesAsync(month);
    }

    [KernelFunction("get_month_summaries")]
    [Description("Gets a summary of all budget months")]
    public async Task<MonthSummaryModel[]> GetMonthSummaryAsync()
    {
        return await _ynabClient.GetMonthSummariesAsync();
    }

    [KernelFunction("get_transactions")]
    [Description("Gets transactions, newest first, for the last 30 days unless 'sinceDate' is given. Optionally filter by account, category, or payee name. Each transaction includes its approval status, so use this for review questions too; prefer get_categories for category spending totals.")]
    public async Task<TransactionModel[]> GetTransactionsAsync(
        [Description("Only return transactions on or after this date. Defaults to 30 days ago.")] DateTimeOffset? sinceDate = null,
        [Description("Only return transactions in this account.")] string? accountName = null,
        [Description("Only return transactions in this budget category.")] string? categoryName = null,
        [Description("Only return transactions for this payee.")] string? payeeName = null)
    {
        return await _ynabClient.GetTransactionsAsync(sinceDate, accountName, categoryName, payeeName);
    }

    [KernelFunction("get_scheduled_transactions")]
    [Description("Gets upcoming scheduled (recurring) transactions such as bills, ordered by next date")]
    public async Task<ScheduledTransactionModel[]> GetScheduledTransactionsAsync()
    {
        return await _ynabClient.GetScheduledTransactionsAsync();
    }
}
