using System.Globalization;
using FireflyCategorizer.Models;

namespace FireflyCategorizer.Services;

public sealed partial class FireflyService
{
    public async Task<IReadOnlyList<string>> GetOwnAccountsAsync(CancellationToken cancellationToken = default)
    {
        return (await GetNamedMapAsync("/api/v1/accounts?type=asset", cancellationToken))
            .Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<TransactionAnalyticsResponse> GetTransactionAnalyticsAsync(int? months = 12, string? ownAccountName = null, CancellationToken cancellationToken = default)
    {
        var normalizedAccountName = string.IsNullOrWhiteSpace(ownAccountName) ? null : ownAccountName.Trim();
        var (startDate, endDate) = GetAnalyticsDateRange(months);
        var rows = new List<AnalyticsTransactionRow>();

        foreach (var transactionType in new[] { "withdrawal", "deposit", "transfer" })
        {
            rows.AddRange(await GetTransactionsByTypeAsync(transactionType, startDate, endDate, cancellationToken));
        }

        var filteredRows = rows
            .Where(row => normalizedAccountName is null
                || string.Equals(row.SourceName, normalizedAccountName, StringComparison.Ordinal)
                || string.Equals(row.DestinationName, normalizedAccountName, StringComparison.Ordinal))
            .ToList();

        return BuildTransactionAnalytics(filteredRows);
    }

    private static (DateTimeOffset? StartDate, DateTimeOffset? EndDate) GetAnalyticsDateRange(int? months)
    {
        if (months is not > 0)
        {
            return (null, null);
        }

        var today = DateTimeOffset.UtcNow.Date;
        var startDate = today.AddMonths(-months.Value);
        return (startDate, today);
    }

    private static TransactionAnalyticsResponse BuildTransactionAnalytics(IReadOnlyList<AnalyticsTransactionRow> rows)
    {
        var orderedRows = rows
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.TransactionJournalId)
            .ToList();

        var withdrawals = orderedRows.Where(row => row.Type == "withdrawal").ToList();
        var deposits = orderedRows.Where(row => row.Type == "deposit").ToList();
        var transfers = orderedRows.Where(row => row.Type == "transfer").ToList();
        var currencyCode = orderedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.CurrencyCode))
            .GroupBy(row => row.CurrencyCode!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault()
            ?? string.Empty;

        var totalExpenses = withdrawals.Sum(row => row.AbsoluteAmount);
        var totalIncome = deposits.Sum(row => row.AbsoluteAmount);

        return new TransactionAnalyticsResponse
        {
            Summary = new TransactionAnalyticsSummary
            {
                TotalTransactions = orderedRows.Count,
                WithdrawalTransactions = withdrawals.Count,
                DepositTransactions = deposits.Count,
                TransferTransactions = transfers.Count,
                TotalExpenses = totalExpenses,
                TotalIncome = totalIncome,
                NetCashflow = totalIncome - totalExpenses,
                UncategorizedWithdrawals = withdrawals.Count(row => string.IsNullOrWhiteSpace(row.CategoryName)),
                UnbudgetedWithdrawals = withdrawals.Count(row => string.IsNullOrWhiteSpace(row.BudgetName)),
                CurrencyCode = currencyCode,
                FirstTransactionDate = orderedRows.Count == 0 ? null : orderedRows.Min(row => row.Date),
                LastTransactionDate = orderedRows.Count == 0 ? null : orderedRows.Max(row => row.Date),
            },
            MonthlyCashflow = orderedRows
                .Where(row => row.Date > DateTimeOffset.MinValue)
                .GroupBy(row => new { row.Date.Year, row.Date.Month })
                .OrderBy(group => group.Key.Year)
                .ThenBy(group => group.Key.Month)
                .TakeLast(12)
                .Select(group => new TransactionMonthlySnapshot
                {
                    Label = new DateTime(group.Key.Year, group.Key.Month, 1).ToString("MMM yy", CultureInfo.InvariantCulture),
                    Income = group.Where(row => row.Type == "deposit").Sum(row => row.AbsoluteAmount),
                    Expenses = group.Where(row => row.Type == "withdrawal").Sum(row => row.AbsoluteAmount),
                    Count = group.Count(),
                })
                .ToList(),
            TopCategories = withdrawals
                .GroupBy(row => string.IsNullOrWhiteSpace(row.CategoryName) ? "Uncategorized" : row.CategoryName!, StringComparer.Ordinal)
                .Select(group => new TransactionAnalyticsPoint
                {
                    Label = group.Key,
                    Amount = group.Sum(row => row.AbsoluteAmount),
                    Count = group.Count(),
                })
                .OrderByDescending(item => item.Amount)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Take(8)
                .ToList(),
            TopBudgets = withdrawals
                .GroupBy(row => string.IsNullOrWhiteSpace(row.BudgetName) ? "No budget" : row.BudgetName!, StringComparer.Ordinal)
                .Select(group => new TransactionAnalyticsPoint
                {
                    Label = group.Key,
                    Amount = group.Sum(row => row.AbsoluteAmount),
                    Count = group.Count(),
                })
                .OrderByDescending(item => item.Amount)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Take(8)
                .ToList(),
            TopIncomeSources = deposits
                .GroupBy(row => string.IsNullOrWhiteSpace(row.SourceName) ? "Unknown source" : row.SourceName!, StringComparer.Ordinal)
                .Select(group => new TransactionAnalyticsPoint
                {
                    Label = group.Key,
                    Amount = group.Sum(row => row.AbsoluteAmount),
                    Count = group.Count(),
                })
                .OrderByDescending(item => item.Amount)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Take(8)
                .ToList(),
            TopDestinations = orderedRows
                .Where(row => row.Type is "withdrawal" or "deposit")
                .GroupBy(row => GetCounterpartyLabel(row), StringComparer.Ordinal)
                .Select(group => new TransactionAnalyticsPoint
                {
                    Label = group.Key,
                    Amount = group.Sum(row => row.AbsoluteAmount),
                    Count = group.Count(),
                })
                .OrderByDescending(item => item.Amount)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Take(8)
                .ToList(),
            TypeBreakdown =
            [
                new TransactionTypeSnapshot { Type = "Withdrawal", Count = withdrawals.Count, Amount = totalExpenses },
                new TransactionTypeSnapshot { Type = "Deposit", Count = deposits.Count, Amount = totalIncome },
                new TransactionTypeSnapshot { Type = "Transfer", Count = transfers.Count, Amount = transfers.Sum(row => row.AbsoluteAmount) },
            ],
            RecentTransactions = orderedRows
                .Take(12)
                .Select(row => new TransactionAnalyticsListItem
                {
                    Type = row.Type,
                    Date = row.RawDate,
                    Description = row.Description,
                    Counterparty = GetCounterpartyLabel(row),
                    Amount = row.AbsoluteAmount,
                    CurrencyCode = row.CurrencyCode,
                    Category = row.CategoryName,
                    Budget = row.BudgetName,
                })
                .ToList(),
        };
    }

    private async Task<List<AnalyticsTransactionRow>> GetTransactionsByTypeAsync(string transactionType, DateTimeOffset? startDate, DateTimeOffset? endDate, CancellationToken cancellationToken)
    {
        var results = new List<AnalyticsTransactionRow>();
        var seenJournalIds = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;

        while (true)
        {
            var envelope = await GetTransactionsPageAsync(transactionType, page, AnalyticsPageSize, startDate, endDate, cancellationToken);

            foreach (var group in envelope.Data)
            {
                foreach (var transaction in group.Attributes.Transactions.Where(item => string.Equals(item.Type, transactionType, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!seenJournalIds.Add(transaction.TransactionJournalId))
                    {
                        continue;
                    }

                    results.Add(new AnalyticsTransactionRow
                    {
                        TransactionJournalId = transaction.TransactionJournalId,
                        Type = transactionType,
                        Date = ParseDate(transaction.Date),
                        RawDate = transaction.Date,
                        Description = transaction.Description,
                        DestinationName = transaction.DestinationName,
                        SourceName = transaction.SourceName,
                        CategoryName = transaction.CategoryName,
                        BudgetName = transaction.BudgetName,
                        CurrencyCode = transaction.CurrencyCode,
                        AbsoluteAmount = ParseAmount(transaction.Amount),
                    });
                }
            }

            if (envelope.Data.Count == 0 || envelope.Meta?.Pagination is null || page >= envelope.Meta.Pagination.TotalPages)
            {
                break;
            }

            page += 1;
        }

        return results;
    }

    private sealed class AnalyticsTransactionRow
    {
        public string TransactionJournalId { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public DateTimeOffset Date { get; init; }
        public string? RawDate { get; init; }
        public string? Description { get; init; }
        public string? DestinationName { get; init; }
        public string? SourceName { get; init; }
        public string? CategoryName { get; init; }
        public string? BudgetName { get; init; }
        public string? CurrencyCode { get; init; }
        public decimal AbsoluteAmount { get; init; }
    }
}