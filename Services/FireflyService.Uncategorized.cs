using FireflyCategorizer.Models;

namespace FireflyCategorizer.Services;

public sealed partial class FireflyService
{
    public async Task<UncategorizedTransactionsResponse> GetUncategorizedTransactionsAsync(int page = 1, int limit = 20, string? destinationName = null, CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedLimit = Math.Clamp(limit, 1, 100);
        var normalizedDestination = string.IsNullOrWhiteSpace(destinationName) ? null : destinationName.Trim();

        EnsureUncategorizedCacheFresh();
        var startIndex = (normalizedPage - 1) * normalizedLimit;
        IReadOnlyList<UncategorizedTransaction> filteredItems;

        if (normalizedDestination is not null)
        {
            await EnsureUncategorizedCacheCompleteAsync(cancellationToken);
            EnsureUncategorizedItemsSorted();
            filteredItems = (_uncategorizedCache?.Items ?? []).Where(item => item.DestinationName == normalizedDestination).ToList();
        }
        else
        {
            var requiredCount = startIndex + normalizedLimit + 1;
            await EnsureUncategorizedCacheCountAsync(requiredCount, cancellationToken);
            EnsureUncategorizedItemsSorted();
            filteredItems = _uncategorizedCache?.Items ?? [];
        }

        var items = filteredItems.Skip(startIndex).Take(normalizedLimit + 1).ToList();
        return new UncategorizedTransactionsResponse
        {
            Page = normalizedPage,
            Limit = normalizedLimit,
            HasNextPage = items.Count > normalizedLimit,
            Items = items.Take(normalizedLimit).ToList(),
            TotalTransactions = filteredItems.Count,
            TotalPages = Math.Max(normalizedPage, (int)Math.Ceiling(filteredItems.Count / (double)normalizedLimit)),
            Complete = normalizedDestination is not null || _uncategorizedCache?.Finished == true,
        };
    }

    public async Task<UncategorizedMetadataResponse> GetUncategorizedMetadataAsync(string? destinationName = null, CancellationToken cancellationToken = default)
    {
        EnsureUncategorizedCacheFresh();
        await EnsureUncategorizedCacheCountAsync(1, cancellationToken);
        StartUncategorizedBackgroundScan(cancellationToken);

        var normalizedDestination = string.IsNullOrWhiteSpace(destinationName) ? null : destinationName.Trim();
        var destinationSummaries = _uncategorizedCache?.DestinationSummaries ?? new Dictionary<string, MetadataSummary>();
        var filteredSummary = normalizedDestination is null
            ? _uncategorizedCache?.Totals ?? new MetadataSummary()
            : destinationSummaries.GetValueOrDefault(normalizedDestination) ?? new MetadataSummary();

        filteredSummary.DestinationName = normalizedDestination;

        return new UncategorizedMetadataResponse
        {
            Summary = filteredSummary,
            Destinations = destinationSummaries.Keys.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            Complete = _uncategorizedCache?.Finished == true,
        };
    }

    public async Task<IReadOnlyList<DestinationRankItem>> GetTopDestinationsAsync(int count = 10, CancellationToken cancellationToken = default)
    {
        EnsureUncategorizedCacheFresh();
        await EnsureUncategorizedCacheCountAsync(1, cancellationToken);

        var summaries = _uncategorizedCache?.DestinationSummaries ?? [];

        return summaries
            .Select(pair => new DestinationRankItem
            {
                Name = pair.Key,
                WithoutCategory = pair.Value.WithoutCategory,
                WithoutBudget = pair.Value.WithoutBudget,
            })
            .Where(item => item.MissingTotal > 0)
            .OrderByDescending(item => item.MissingTotal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyList<UncategorizedTransaction>> GetUncategorizedTransactionsForDestinationAsync(string destinationName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationName))
        {
            return [];
        }

        EnsureUncategorizedCacheFresh();
        await EnsureUncategorizedCacheCompleteAsync(cancellationToken);
        EnsureUncategorizedItemsSorted();
        return (_uncategorizedCache?.Items ?? []).Where(item => item.DestinationName == destinationName.Trim()).ToList();
    }

    public async Task<IReadOnlyList<RecentTransaction>> GetRecentTransactionsForDestinationAsync(string destinationName, int limit = 5, string? excludeJournalId = null, CancellationToken cancellationToken = default)
    {
        var query = $"to:\"{EscapeSearchValue(destinationName)}\"";
        var envelope = await GetSearchTransactionsAsync(query, page: 1, limit: Math.Max(limit * 4, 20), cancellationToken);

        return envelope.Data
            .SelectMany(group => group.Attributes.Transactions)
            .Where(transaction => transaction.Type == "withdrawal"
                && transaction.DestinationName == destinationName
                && transaction.TransactionJournalId != excludeJournalId
                && (!string.IsNullOrWhiteSpace(transaction.CategoryName) || !string.IsNullOrWhiteSpace(transaction.BudgetName)))
            .OrderByDescending(transaction => ParseDate(transaction.Date))
            .Take(limit)
            .Select(transaction => new RecentTransaction(
                transaction.TransactionJournalId,
                transaction.Date,
                transaction.Description,
                transaction.DestinationName,
                transaction.Amount,
                transaction.CurrencyCode,
                transaction.CategoryName,
                transaction.BudgetName,
                transaction.Type))
            .ToList();
    }

    private void EnsureUncategorizedCacheFresh()
    {
        if (_uncategorizedCache is not null && DateTimeOffset.UtcNow - _uncategorizedCache.CreatedAt < UncategorizedCacheTtl)
        {
            return;
        }

        _uncategorizedCache = new UncategorizedCache
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Sources = UncategorizedSources.Select(source => new UncategorizedSourceState
            {
                Id = source.Id,
                Query = source.Query,
            }).ToList(),
        };
    }

    private void StartUncategorizedBackgroundScan(CancellationToken cancellationToken)
    {
        if (_uncategorizedCache is null || _uncategorizedCache.Finished || _uncategorizedBackgroundScanTask is not null)
        {
            return;
        }

        _uncategorizedBackgroundScanTask = Task.Run(async () =>
        {
            try
            {
                await EnsureUncategorizedCacheCompleteAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not complete uncategorized metadata scan.");
            }
            finally
            {
                _uncategorizedBackgroundScanTask = null;
            }
        }, cancellationToken);
    }

    private async Task EnsureUncategorizedCacheCountAsync(int requiredCount, CancellationToken cancellationToken)
    {
        while (true)
        {
            var cache = _uncategorizedCache;
            if (cache is null || cache.Finished || cache.Items.Count >= requiredCount)
            {
                return;
            }

            Task currentTask;
            await _scanStateLock.WaitAsync(cancellationToken);
            try
            {
                _uncategorizedScanTask ??= ScanNextUncategorizedPageAsync(cancellationToken);
                currentTask = _uncategorizedScanTask;
            }
            finally
            {
                _scanStateLock.Release();
            }

            try
            {
                await currentTask;
            }
            finally
            {
                await _scanStateLock.WaitAsync(cancellationToken);
                try
                {
                    if (ReferenceEquals(_uncategorizedScanTask, currentTask) && currentTask.IsCompleted)
                    {
                        _uncategorizedScanTask = null;
                    }
                }
                finally
                {
                    _scanStateLock.Release();
                }
            }
        }
    }

    private Task EnsureUncategorizedCacheCompleteAsync(CancellationToken cancellationToken)
        => EnsureUncategorizedCacheCountAsync(int.MaxValue, cancellationToken);

    private async Task ScanNextUncategorizedPageAsync(CancellationToken cancellationToken)
    {
        if (_uncategorizedCache is null || _uncategorizedCache.Finished)
        {
            return;
        }

        var cache = _uncategorizedCache;
        var pendingSources = cache.Sources.Where(source => !source.Finished).ToList();
        if (pendingSources.Count == 0)
        {
            cache.Finished = true;
            return;
        }

        await Task.WhenAll(pendingSources.Select(source => ScanUncategorizedSourcePageAsync(cache, source, cancellationToken)));
        cache.Finished = cache.Sources.All(source => source.Finished);
    }

    private async Task ScanUncategorizedSourcePageAsync(UncategorizedCache cache, UncategorizedSourceState source, CancellationToken cancellationToken)
    {
        var envelope = await GetSearchTransactionsAsync(source.Query, source.NextPage, UncategorizedPageSize, cancellationToken);
        if (!ReferenceEquals(_uncategorizedCache, cache))
        {
            return;
        }

        foreach (var group in envelope.Data)
        {
            foreach (var transaction in group.Attributes.Transactions)
            {
                if (!string.Equals(transaction.Type, "withdrawal", StringComparison.Ordinal))
                {
                    continue;
                }

                if (source.Id == "missing-category" && transaction.CategoryName is not null)
                {
                    continue;
                }

                if (source.Id == "missing-budget" && transaction.BudgetName is not null)
                {
                    continue;
                }

                var transactionJournalId = transaction.TransactionJournalId;
                if (cache.ItemsByJournalId.TryGetValue(transactionJournalId, out var existingItem))
                {
                    existingItem.Category ??= transaction.CategoryName;
                    existingItem.Budget ??= transaction.BudgetName;
                    existingItem.Tags = existingItem.Tags.Concat(transaction.Tags).Distinct().ToList();
                    existingItem.Transactions = group.Attributes.Transactions;
                    continue;
                }

                var item = new UncategorizedTransaction
                {
                    TransactionId = group.Id,
                    TransactionJournalId = transactionJournalId,
                    Date = transaction.Date,
                    Description = transaction.Description,
                    DestinationName = transaction.DestinationName,
                    SourceName = transaction.SourceName,
                    Amount = transaction.Amount,
                    CurrencyCode = transaction.CurrencyCode,
                    Category = transaction.CategoryName,
                    Budget = transaction.BudgetName,
                    Tags = [.. transaction.Tags],
                    Transactions = group.Attributes.Transactions,
                };

                cache.ItemsByJournalId[transactionJournalId] = item;
                cache.Items.Add(item);
                cache.ItemsSorted = false;
                AddMetadataEntry(cache, item);
            }
        }

        if (envelope.Data.Count == 0 || envelope.Meta?.Pagination is null || source.NextPage >= envelope.Meta.Pagination.TotalPages)
        {
            source.Finished = true;
        }
        else
        {
            source.NextPage += 1;
        }
    }

    private void AddMetadataEntry(UncategorizedCache cache, UncategorizedTransaction item)
    {
        var destinationName = item.DestinationName ?? "Unknown";
        if (!cache.DestinationSummaries.TryGetValue(destinationName, out var summary))
        {
            summary = new MetadataSummary();
            cache.DestinationSummaries[destinationName] = summary;
        }

        summary.TotalTransactions += 1;
        cache.Totals.TotalTransactions += 1;

        if (item.Category is null)
        {
            summary.WithoutCategory += 1;
            cache.Totals.WithoutCategory += 1;
        }

        if (item.Budget is null)
        {
            summary.WithoutBudget += 1;
            cache.Totals.WithoutBudget += 1;
        }
    }

    private void EnsureUncategorizedItemsSorted()
    {
        if (_uncategorizedCache is null || _uncategorizedCache.ItemsSorted)
        {
            return;
        }

        _uncategorizedCache.Items.Sort((left, right) =>
        {
            var dateDifference = ParseDate(right.Date).CompareTo(ParseDate(left.Date));
            return dateDifference != 0 ? dateDifference : string.CompareOrdinal(right.TransactionJournalId, left.TransactionJournalId);
        });

        _uncategorizedCache.ItemsSorted = true;
    }

    private void InvalidateUncategorizedCache()
    {
        _uncategorizedCache = null;
        _uncategorizedBackgroundScanTask = null;
    }

    private sealed class UncategorizedCache
    {
        public DateTimeOffset CreatedAt { get; init; }
        public List<UncategorizedTransaction> Items { get; } = [];
        public Dictionary<string, UncategorizedTransaction> ItemsByJournalId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MetadataSummary> DestinationSummaries { get; } = new(StringComparer.Ordinal);
        public MetadataSummary Totals { get; } = new();
        public List<UncategorizedSourceState> Sources { get; init; } = [];
        public bool ItemsSorted { get; set; }
        public bool Finished { get; set; }
    }

    private sealed class UncategorizedSourceState
    {
        public string Id { get; init; } = string.Empty;
        public string Query { get; init; } = string.Empty;
        public int NextPage { get; set; } = 1;
        public bool Finished { get; set; }
    }
}