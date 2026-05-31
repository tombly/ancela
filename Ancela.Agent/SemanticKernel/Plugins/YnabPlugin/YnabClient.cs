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

    public async Task<AccountsSummaryModel> GetAccountsAsync()
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var accounts = (await _client.GetAccountsAsync(budgetDetail.Id.ToString(), null)!).Data.Accounts;

        var models = accounts
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

        // YNAB has no net-worth endpoint, so compute it here rather than make the model
        // sum the list. Liabilities carry negative balances, so NetWorth is a plain sum.
        return new AccountsSummaryModel
        {
            NetWorth = models.Sum(a => a.Balance),
            TotalAssets = models.Where(a => a.Balance > 0).Sum(a => a.Balance),
            TotalLiabilities = models.Where(a => a.Balance < 0).Sum(a => a.Balance),
            Accounts = models,
        };
    }

    /// <param name="month">
    /// Any date within the target month; normalized to the first of the month. Null returns
    /// the current month.
    /// </param>
    public async Task<CategoryModel[]> GetCategoriesAsync(DateTimeOffset? month = null)
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var budgetId = budgetDetail.Id.ToString();

        var categories = month is null
            ? (await _client.GetCategoriesAsync(budgetId, null)).Data.Category_groups.SelectMany(g => g.Categories)
            : (await _client.GetBudgetMonthAsync(budgetId, FirstOfMonth(month.Value))).Data.Month.Categories;

        return categories
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

    /// <summary>
    /// Returns transactions newest-first. When <paramref name="categoryName"/> or
    /// <paramref name="payeeName"/> is given the request is filtered server-side (which
    /// correctly expands split transactions); any remaining filters are applied client-side.
    /// </summary>
    public async Task<TransactionModel[]> GetTransactionsAsync(
        DateTimeOffset? sinceDate = null,
        string? accountName = null,
        string? categoryName = null,
        string? payeeName = null)
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var budgetId = budgetDetail.Id.ToString();

        // Default to the last 30 days so an unfiltered query can't pull the entire history.
        sinceDate ??= DateTimeOffset.Now.AddDays(-30);

        string? serverFilter;
        IEnumerable<TransactionModel> transactions;

        if (categoryName is not null)
        {
            serverFilter = "category";
            var response = await _client.GetTransactionsByCategoryAsync(budgetId, ResolveCategoryId(budgetDetail, categoryName), sinceDate, null, null);
            transactions = response.Data.Transactions.Where(t => !t.Deleted).Select(t => Map(t));
        }
        else if (payeeName is not null)
        {
            serverFilter = "payee";
            var response = await _client.GetTransactionsByPayeeAsync(budgetId, ResolvePayeeId(budgetDetail, payeeName), sinceDate, null, null);
            transactions = response.Data.Transactions.Where(t => !t.Deleted).Select(t => Map(t));
        }
        else if (accountName is not null)
        {
            serverFilter = "account";
            var response = await _client.GetTransactionsByAccountAsync(budgetId, ResolveAccountId(budgetDetail, accountName), sinceDate, null, null);
            transactions = response.Data.Transactions.Where(t => !t.Deleted).Select(t => Map(t));
        }
        else
        {
            serverFilter = null;
            var response = await _client.GetTransactionsAsync(budgetId, sinceDate, null, null);
            transactions = response.Data.Transactions.Where(t => !t.Deleted).Select(t => Map(t));
        }

        // Apply any filters the server-side query didn't already satisfy.
        if (accountName is not null && serverFilter != "account")
            transactions = transactions.Where(t => Matches(t.AccountName, accountName));
        if (categoryName is not null && serverFilter != "category")
            transactions = transactions.Where(t => Matches(t.CategoryName, categoryName));
        if (payeeName is not null && serverFilter != "payee")
            transactions = transactions.Where(t => Matches(t.PayeeName, payeeName));

        return transactions.OrderByDescending(t => t.Date).ToArray();
    }

    public async Task<ScheduledTransactionModel[]> GetScheduledTransactionsAsync()
    {
        var budgetDetail = await _client.GetBudgetDetailAsync();
        var scheduled = await _client.GetScheduledTransactionsAsync(budgetDetail.Id.ToString(), null);
        return scheduled.Data.Scheduled_transactions
                .Where(s => !s.Deleted)
                .Select(s => new ScheduledTransactionModel
                {
                    DateNext = s.Date_next,
                    Frequency = s.Frequency,
                    Amount = s.Amount.FromMilliunits(),
                    PayeeName = s.Payee_name,
                    CategoryName = s.Category_name,
                    AccountName = s.Account_name,
                    Memo = s.Memo,
                })
                .OrderBy(s => s.DateNext)
                .ToArray();
    }

    private static TransactionModel Map(TransactionDetail t) => new()
    {
        Date = t.Date,
        Amount = t.Amount.FromMilliunits(),
        PayeeName = t.Payee_name,
        CategoryName = t.Category_name,
        AccountName = t.Account_name,
        Memo = t.Memo,
        Cleared = t.Cleared,
        Approved = t.Approved,
    };

    private static TransactionModel Map(HybridTransaction t) => new()
    {
        Date = t.Date,
        Amount = t.Amount.FromMilliunits(),
        PayeeName = t.Payee_name,
        CategoryName = t.Category_name,
        AccountName = t.Account_name,
        Memo = t.Memo,
        Cleared = t.Cleared,
        Approved = t.Approved,
    };

    private static string ResolveAccountId(BudgetDetail budget, string name)
    {
        var accounts = budget.Accounts.Where(a => !a.Deleted).ToList();
        var match = accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.InvariantCultureIgnoreCase))
                    ?? accounts.FirstOrDefault(a => Matches(a.Name, name))
                    ?? throw new Exception($"No account found matching '{name}'.");
        return match.Id.ToString();
    }

    private static string ResolveCategoryId(BudgetDetail budget, string name)
    {
        var categories = budget.Categories.Where(c => !c.Deleted && !c.Hidden).ToList();
        var match = categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.InvariantCultureIgnoreCase))
                    ?? categories.FirstOrDefault(c => Matches(c.Name, name))
                    ?? throw new Exception($"No category found matching '{name}'.");
        return match.Id.ToString();
    }

    private static string ResolvePayeeId(BudgetDetail budget, string name)
    {
        var payees = budget.Payees.Where(p => !p.Deleted).ToList();
        var match = payees.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.InvariantCultureIgnoreCase))
                    ?? payees.FirstOrDefault(p => Matches(p.Name, name))
                    ?? throw new Exception($"No payee found matching '{name}'.");
        return match.Id.ToString();
    }

    private static bool Matches(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.InvariantCultureIgnoreCase);

    private static DateTimeOffset FirstOfMonth(DateTimeOffset date) =>
        new(date.Year, date.Month, 1, 0, 0, 0, date.Offset);
}
