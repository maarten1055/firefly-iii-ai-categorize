using FireflyCategorizer.DotNet.Models;

namespace FireflyCategorizer.DotNet.Services;

public sealed class CategorizationCoordinator
{
    private static readonly HashSet<string> ValidTransactionTriggers =
    [
        "STORE_TRANSACTION",
        "TRIGGER_STORE_TRANSACTION",
        "UPDATE_TRANSACTION",
        "TRIGGER_UPDATE_TRANSACTION",
    ];

    private static readonly HashSet<string> ValidTransactionResponses = ["TRANSACTIONS", "RESPONSE_TRANSACTIONS"];
    private const int DestinationHistoryLimit = 5;

    private readonly FireflyService _fireflyService;
    private readonly AiClassifierService _aiClassifierService;
    private readonly JobStore _jobStore;
    private readonly JobQueue _jobQueue;
    private readonly ILogger<CategorizationCoordinator> _logger;

    public CategorizationCoordinator(
        FireflyService fireflyService,
        AiClassifierService aiClassifierService,
        JobStore jobStore,
        JobQueue jobQueue,
        ILogger<CategorizationCoordinator> logger)
    {
        _fireflyService = fireflyService;
        _aiClassifierService = aiClassifierService;
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _logger = logger;
    }

    public async Task<ClassificationContext> ClassifyTransactionSelectionAsync(UncategorizedTransaction transaction, CancellationToken cancellationToken)
    {
        var destinationName = transaction.DestinationName;
        var description = transaction.Description;

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("Missing transaction.description");
        }

        if (string.IsNullOrWhiteSpace(destinationName))
        {
            throw new InvalidOperationException("Missing transaction.destinationName");
        }

        var categories = await _fireflyService.GetCategoriesAsync(cancellationToken);
        var budgets = await _fireflyService.GetBudgetsAsync(cancellationToken);

        IReadOnlyList<RecentTransaction> recentTransactions = [];
        try
        {
            recentTransactions = await _fireflyService.GetRecentTransactionsForDestinationAsync(destinationName, DestinationHistoryLimit, transaction.TransactionJournalId, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not load recent transactions for destination {DestinationName}.", destinationName);
        }

        var classification = await _aiClassifierService.ClassifyAsync(categories.Keys.ToList(), budgets.Keys.ToList(), destinationName, description, recentTransactions, cancellationToken);
        var data = new ClassificationResult
        {
            Category = transaction.Category ?? classification?.Category,
            Budget = transaction.Budget ?? classification?.Budget,
            Prompt = classification?.Prompt,
            Response = classification?.Response,
            HistoryCount = recentTransactions.Count,
            Error = null,
            Status = GetClassificationStatus(transaction.Category ?? classification?.Category, transaction.Budget ?? classification?.Budget),
            CanUpdate = !string.IsNullOrWhiteSpace(transaction.Category ?? classification?.Category) || !string.IsNullOrWhiteSpace(transaction.Budget ?? classification?.Budget),
        };

        return new ClassificationContext
        {
            Categories = categories,
            Budgets = budgets,
            Data = data,
        };
    }

    public async Task<ClassificationResult> ClassifyForUiAsync(UncategorizedTransaction transaction, CancellationToken cancellationToken)
    {
        var context = await ClassifyTransactionSelectionAsync(transaction, cancellationToken);
        return context.Data;
    }

    public async Task<(UncategorizedTransaction Transaction, string Status)> ApplyTransactionUpdateAsync(ApplyTransactionRequest request, CancellationToken cancellationToken)
    {
        var transaction = request.Transaction ?? throw new InvalidOperationException("Missing transaction payload.");
        if (string.IsNullOrWhiteSpace(transaction.TransactionId) || transaction.Transactions.Count == 0)
        {
            throw new InvalidOperationException("This transaction does not contain enough Firefly III data to update.");
        }

        var categories = await _fireflyService.GetCategoriesAsync(cancellationToken);
        var budgets = await _fireflyService.GetBudgetsAsync(cancellationToken);
        var currentCategory = transaction.Category;
        var currentBudget = transaction.Budget;
        var selectedCategory = currentCategory ?? request.Selections.Category ?? request.Classification?.Category;
        var selectedBudget = currentBudget ?? request.Selections.Budget ?? request.Classification?.Budget;
        var categoryId = currentCategory is null && selectedCategory is not null && categories.TryGetValue(selectedCategory, out var resolvedCategoryId)
            ? resolvedCategoryId
            : null;
        var budgetId = currentBudget is null && selectedBudget is not null && budgets.TryGetValue(selectedBudget, out var resolvedBudgetId)
            ? resolvedBudgetId
            : null;

        if (categoryId is null && budgetId is null)
        {
            throw new InvalidOperationException("No valid category or budget is available to update.");
        }

        await _fireflyService.SetCategoryAndBudgetAsync(transaction.TransactionId, transaction.Transactions, categoryId, budgetId, cancellationToken);

        transaction.Category = currentCategory ?? selectedCategory;
        transaction.Budget = currentBudget ?? selectedBudget;
        return (transaction, GetClassificationStatus(transaction.Category, transaction.Budget));
    }

    public async Task<int> ApplyDestinationUpdateAsync(string destination, SelectionValues selections, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("Missing destination.");
        }

        if (string.IsNullOrWhiteSpace(selections.Category) && string.IsNullOrWhiteSpace(selections.Budget))
        {
            throw new InvalidOperationException("Select at least one category or budget value.");
        }

        var categories = await _fireflyService.GetCategoriesAsync(cancellationToken);
        var budgets = await _fireflyService.GetBudgetsAsync(cancellationToken);
        var transactions = await _fireflyService.GetUncategorizedTransactionsForDestinationAsync(destination, cancellationToken);
        var updatedCount = 0;

        foreach (var transaction in transactions)
        {
            var categoryId = transaction.Category is null && selections.Category is not null && categories.TryGetValue(selections.Category, out var resolvedCategoryId)
                ? resolvedCategoryId
                : null;
            var budgetId = transaction.Budget is null && selections.Budget is not null && budgets.TryGetValue(selections.Budget, out var resolvedBudgetId)
                ? resolvedBudgetId
                : null;

            if (categoryId is null && budgetId is null)
            {
                continue;
            }

            await _fireflyService.SetCategoryAndBudgetAsync(transaction.TransactionId, transaction.Transactions, categoryId, budgetId, cancellationToken);
            updatedCount += 1;
        }

        return updatedCount;
    }

    public async Task<JobRecord> FillMissingJobValueAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = _jobStore.GetJob(jobId) ?? throw new KeyNotFoundException($"Job {jobId} was not found.");
        var categoryName = job.Data.Category;
        var budgetName = job.Data.Budget;

        if ((string.IsNullOrWhiteSpace(categoryName) && string.IsNullOrWhiteSpace(budgetName)) || (!string.IsNullOrWhiteSpace(categoryName) && !string.IsNullOrWhiteSpace(budgetName)))
        {
            throw new InvalidOperationException("This job is not partially processed.");
        }

        if (string.IsNullOrWhiteSpace(job.Data.TransactionId) || job.Data.Transactions.Count == 0)
        {
            throw new InvalidOperationException("This job does not contain enough transaction data to update Firefly III.");
        }

        var categories = await _fireflyService.GetCategoriesAsync(cancellationToken);
        var budgets = await _fireflyService.GetBudgetsAsync(cancellationToken);
        string? categoryId = null;
        string? budgetId = null;
        string copiedValue;
        string updatedField;

        if (!string.IsNullOrWhiteSpace(categoryName) && string.IsNullOrWhiteSpace(budgetName))
        {
            if (!budgets.TryGetValue(categoryName, out budgetId))
            {
                throw new InvalidOperationException($"No budget exists with the same name as category '{categoryName}'.");
            }

            copiedValue = categoryName;
            updatedField = "budget";
        }
        else
        {
            if (!categories.TryGetValue(budgetName!, out categoryId))
            {
                throw new InvalidOperationException($"No category exists with the same name as budget '{budgetName}'.");
            }

            copiedValue = budgetName!;
            updatedField = "category";
        }

        await _fireflyService.SetCategoryAndBudgetAsync(job.Data.TransactionId!, job.Data.Transactions, categoryId, budgetId, cancellationToken);
        job.Data.Category ??= copiedValue;
        job.Data.Budget ??= copiedValue;
        job.Data.Error = null;
        job.Data.ManualUpdate = $"Filled missing {updatedField} with '{copiedValue}'.";
        await _jobStore.UpdateJobDataAsync(job.Id, job.Data, cancellationToken);
        await _jobStore.SetJobFinishedAsync(job.Id, GetCompletionStatus(job.Data), cancellationToken);
        return job;
    }

    public async Task QueueWebhookAsync(WebhookPayload payload, CancellationToken cancellationToken)
    {
        ValidateWebhookPayload(payload);
        var transaction = payload.Content!.Transactions[0];

        var job = await _jobStore.CreateJobAsync(new JobData
        {
            DestinationName = transaction.DestinationName,
            Description = transaction.Description,
            TransactionId = payload.Content.Id,
            Transactions = payload.Content.Transactions,
            Category = transaction.CategoryName,
            Budget = transaction.BudgetName,
            Error = null,
            ManualUpdate = null,
        }, cancellationToken);

        await _jobQueue.EnqueueAsync(async token =>
        {
            await _jobStore.SetJobInProgressAsync(job.Id, token);

            try
            {
                var transactionSelection = new UncategorizedTransaction
                {
                    TransactionId = payload.Content.Id!,
                    TransactionJournalId = transaction.TransactionJournalId,
                    Description = transaction.Description,
                    DestinationName = transaction.DestinationName,
                    Category = transaction.CategoryName,
                    Budget = transaction.BudgetName,
                    Transactions = payload.Content.Transactions,
                };

                var context = await ClassifyTransactionSelectionAsync(transactionSelection, token);
                job.Data.Category = context.Data.Category;
                job.Data.Budget = context.Data.Budget;
                job.Data.Prompt = context.Data.Prompt;
                job.Data.Response = context.Data.Response;
                job.Data.HistoryCount = context.Data.HistoryCount;
                job.Data.Error = null;
                job.Data.ManualUpdate = null;

                await _jobStore.UpdateJobDataAsync(job.Id, job.Data, token);

                var categoryId = transaction.CategoryName is null && context.Data.Category is not null && context.Categories.TryGetValue(context.Data.Category, out var resolvedCategoryId)
                    ? resolvedCategoryId
                    : null;
                var budgetId = transaction.BudgetName is null && context.Data.Budget is not null && context.Budgets.TryGetValue(context.Data.Budget, out var resolvedBudgetId)
                    ? resolvedBudgetId
                    : null;

                if (categoryId is not null || budgetId is not null)
                {
                    await _fireflyService.SetCategoryAndBudgetAsync(payload.Content.Id!, payload.Content.Transactions, categoryId, budgetId, token);
                }

                await _jobStore.SetJobFinishedAsync(job.Id, GetCompletionStatus(job.Data), token);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Job {JobId} failed.", job.Id);
                await _jobStore.SetJobFailedAsync(job.Id, exception.Message, token);
            }
        }, cancellationToken);
    }

    private static void ValidateWebhookPayload(WebhookPayload payload)
    {
        if (!ValidTransactionTriggers.Contains(payload.Trigger ?? string.Empty))
        {
            throw new InvalidOperationException($"Unsupported webhook trigger '{payload.Trigger}'.");
        }

        if (!ValidTransactionResponses.Contains(payload.Response ?? string.Empty))
        {
            throw new InvalidOperationException($"Unsupported webhook response '{payload.Response}'.");
        }

        if (string.IsNullOrWhiteSpace(payload.Content?.Id))
        {
            throw new InvalidOperationException("Missing content.id");
        }

        if (payload.Content.Transactions.Count == 0)
        {
            throw new InvalidOperationException("No transactions are available in content.transactions");
        }

        var transaction = payload.Content.Transactions[0];
        if (!string.Equals(transaction.Type, "withdrawal", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("content.transactions[0].type has to be 'withdrawal'. Transaction will be ignored.");
        }

        if (transaction.CategoryName is not null && transaction.BudgetName is not null)
        {
            throw new InvalidOperationException("content.transactions[0].category_name and content.transactions[0].budget_name are already set. Transaction will be ignored.");
        }

        if (string.IsNullOrWhiteSpace(transaction.Description))
        {
            throw new InvalidOperationException("Missing content.transactions[0].description");
        }

        if (string.IsNullOrWhiteSpace(transaction.DestinationName))
        {
            throw new InvalidOperationException("Missing content.transactions[0].destination_name");
        }
    }

    public static string GetClassificationStatus(string? category, string? budget)
    {
        var hasCategory = !string.IsNullOrWhiteSpace(category);
        var hasBudget = !string.IsNullOrWhiteSpace(budget);

        if (hasCategory && hasBudget)
        {
            return "ready";
        }

        if (hasCategory || hasBudget)
        {
            return "partial";
        }

        return "unclassified";
    }

    public static string GetCompletionStatus(JobData data)
    {
        var hasCategory = !string.IsNullOrWhiteSpace(data.Category);
        var hasBudget = !string.IsNullOrWhiteSpace(data.Budget);
        return hasCategory != hasBudget ? "partial" : "finished";
    }
}