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
/// </summary>
public class YnabPlugin(YnabClient _ynabClient)
{
    [KernelFunction("get_accounts")]
    [Description("Gets a list of account balances")]
    public async Task<AccountModel[]> GetAccountsAsync()
    {
        return await _ynabClient.GetAccountsAsync();
    }

    [KernelFunction("get_categories")]
    [Description("Gets a list of budget categories")]
    public async Task<CategoryModel[]> GetCategoriesAsync()
    {
        return await _ynabClient.GetCategoriesAsync();
    }

    [KernelFunction("get_month_summaries")]
    [Description("Gets a summary of all budget months")]
    public async Task<MonthSummaryModel[]> GetMonthSummaryAsync()
    {
        return await _ynabClient.GetMonthSummariesAsync();
    }
}
