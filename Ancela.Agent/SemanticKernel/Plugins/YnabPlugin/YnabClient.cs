using System.Net.Http.Headers;
using Ancela.Agent.SemanticKernel.Plugins.YnabPlugin.Models;
using Ynab.Api.Client;
using Ynab.Api.Client.Extensions;

namespace Ancela.Agent.SemanticKernel.Plugins.YnabPlugin;

public class YnabClient
{
    private readonly YnabApiClient _client;

    public YnabClient()
    {
        var ynabAccessToken = Environment.GetEnvironmentVariable("YNAB_ACCESS_TOKEN") ?? throw new Exception("YNAB_ACCESS_TOKEN not set");

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ynabAccessToken);
        _client = new YnabApiClient(httpClient);
    }

    public async Task<AccountModel[]> GetAccountsAsync()
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var accounts = (await _client.GetAccountsAsync(budgetDetail.Id.ToString(), null)!).Data.Accounts;

        return accounts
                .Where(a => !a.Deleted && !a.Closed)
                .Select(a => new AccountModel
                {
                    Name = a.Name,
                    Type = a.Type,
                    OnBudget = a.On_budget,
                    Note = a.Note,
                    Balance = a.Balance.FromMilliunits(),
                    ClearedBalance = a.Cleared_balance.FromMilliunits(),
                    UnclearedBalance = a.Uncleared_balance.FromMilliunits(),
                    LastReconciledAt = a.Last_reconciled_at.HasValue ? a.Last_reconciled_at.Value.DateTime : null
                }).ToArray();
    }

    public async Task<CategoryModel[]> GetCategoriesAsync()
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var categories = await _client.GetCategoriesAsync(budgetDetail.Id.ToString(), null);

        return categories.Data.Category_groups
                .SelectMany(a => a.Categories)
                .Where(a => !a.Deleted && !a.Hidden)
                .Select(c => new CategoryModel
                {
                    CategoryGroupName = c.Category_group_name,
                    Name = c.Name,
                    Budgeted = c.Budgeted.FromMilliunits(),
                    Activity = c.Activity.FromMilliunits(),
                    Balance = c.Balance.FromMilliunits(),
                    GoalType = c.Goal_type,
                    GoalTarget = c.Goal_target.HasValue ? c.Goal_target.Value.FromMilliunits() : null,
                    GoalPercentageComplete = c.Goal_percentage_complete,
                    MonthlyNeed = c.MonthlyNeed().FromMilliunits(),
                }).ToArray();
    }

    public async Task<MonthSummaryModel[]> GetMonthSummariesAsync()
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var monthSummaries = await _client.GetBudgetMonthsAsync(budgetDetail.Id.ToString(), null);
        return monthSummaries.Data.Months
                .Select(m => new MonthSummaryModel
                {
                    Month = m.Month,
                    Income = m.Income.FromMilliunits(),
                    Budgeted = m.Budgeted.FromMilliunits(),
                    Activity = m.Activity.FromMilliunits(),
                    ReadyToAssign = m.To_be_budgeted.FromMilliunits(),
                    AgeOfMoney = m.Age_of_money
                }).ToArray();
    }
}
