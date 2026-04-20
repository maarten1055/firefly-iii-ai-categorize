using System.Text.Json.Serialization;

namespace FireflyCategorizer.Models;

public sealed class JobRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "queued";
    public JobData Data { get; set; } = new();
}

public sealed class JobData
{
    public string? DestinationName { get; set; }
    public string? Description { get; set; }
    public string? TransactionId { get; set; }
    public List<FireflyTransactionSplit> Transactions { get; set; } = [];
    public string? Category { get; set; }
    public string? Budget { get; set; }
    public string? Prompt { get; set; }
    public string? Response { get; set; }
    public int HistoryCount { get; set; }
    public string? Error { get; set; }
    public string? ManualUpdate { get; set; }
}

public sealed class SelectionValues
{
    public string? Category { get; set; }
    public string? Budget { get; set; }
}

public sealed class ClassificationResult
{
    public string? Category { get; set; }
    public string? Budget { get; set; }
    public string? Prompt { get; set; }
    public string? Response { get; set; }
    public int HistoryCount { get; set; }
    public string? Error { get; set; }
    public string Status { get; set; } = "unclassified";
    public bool CanUpdate { get; set; }
}

public sealed class DiagnosticCheckItem
{
    public string Name { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public Dictionary<string, string> Details { get; set; } = new(StringComparer.Ordinal);
}

public sealed class UncategorizedTransaction
{
    public string TransactionId { get; set; } = string.Empty;
    public string TransactionJournalId { get; set; } = string.Empty;
    public string? Date { get; set; }
    public string? Description { get; set; }
    public string? DestinationName { get; set; }
    public string? SourceName { get; set; }
    public string? Amount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? Category { get; set; }
    public string? Budget { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<FireflyTransactionSplit> Transactions { get; set; } = [];
}

public sealed class MetadataSummary
{
    public int TotalTransactions { get; set; }
    public int WithoutCategory { get; set; }
    public int WithoutBudget { get; set; }
    public string? DestinationName { get; set; }
}

public sealed class UncategorizedTransactionsResponse
{
    public int Page { get; set; }
    public int Limit { get; set; }
    public bool HasNextPage { get; set; }
    public List<UncategorizedTransaction> Items { get; set; } = [];
    public int TotalTransactions { get; set; }
    public int TotalPages { get; set; }
    public bool Complete { get; set; }
}

public sealed class UncategorizedMetadataResponse
{
    public MetadataSummary Summary { get; set; } = new();
    public List<string> Destinations { get; set; } = [];
    public bool Complete { get; set; }
}

public sealed record RecentTransaction(
    string Id,
    string? Date,
    string? Description,
    string? DestinationName,
    string? Amount,
    string? CurrencyCode,
    string? Category,
    string? Budget,
    string? Type);

public sealed class ClassificationContext
{
    public required Dictionary<string, string> Categories { get; init; }
    public required Dictionary<string, string> Budgets { get; init; }
    public required ClassificationResult Data { get; init; }
}

public sealed class ClassifyTransactionRequest
{
    public UncategorizedTransaction? Transaction { get; set; }
}

public sealed class ApplyTransactionRequest
{
    public UncategorizedTransaction? Transaction { get; set; }
    public ClassificationResult? Classification { get; set; }
    public SelectionValues Selections { get; set; } = new();
}

public sealed class ApplyDestinationRequest
{
    public string? Destination { get; set; }
    public SelectionValues Selections { get; set; } = new();
}

public sealed class DestinationRankItem
{
    public string Name { get; init; } = string.Empty;
    public int WithoutCategory { get; init; }
    public int WithoutBudget { get; init; }
    public int MissingTotal => WithoutCategory + WithoutBudget;
}

public sealed class TransactionAnalyticsSummary
{
    public int TotalTransactions { get; set; }
    public int WithdrawalTransactions { get; set; }
    public int DepositTransactions { get; set; }
    public int TransferTransactions { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal TotalIncome { get; set; }
    public decimal NetCashflow { get; set; }
    public int UncategorizedWithdrawals { get; set; }
    public int UnbudgetedWithdrawals { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public DateTimeOffset? FirstTransactionDate { get; set; }
    public DateTimeOffset? LastTransactionDate { get; set; }
}

public sealed class TransactionAnalyticsPoint
{
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Count { get; set; }
}

public sealed class TransactionMonthlySnapshot
{
    public string Label { get; set; } = string.Empty;
    public decimal Income { get; set; }
    public decimal Expenses { get; set; }
    public decimal Net => Income - Expenses;
    public int Count { get; set; }
}

public sealed class TransactionTypeSnapshot
{
    public string Type { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Amount { get; set; }
}

public sealed class TransactionAnalyticsListItem
{
    public string Type { get; set; } = string.Empty;
    public string? Date { get; set; }
    public string? Description { get; set; }
    public string? Counterparty { get; set; }
    public decimal Amount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? Category { get; set; }
    public string? Budget { get; set; }
}

public sealed class TransactionAnalyticsResponse
{
    public TransactionAnalyticsSummary Summary { get; set; } = new();
    public List<TransactionMonthlySnapshot> MonthlyCashflow { get; set; } = [];
    public List<TransactionAnalyticsPoint> TopCategories { get; set; } = [];
    public List<TransactionAnalyticsPoint> TopBudgets { get; set; } = [];
    public List<TransactionAnalyticsPoint> TopIncomeSources { get; set; } = [];
    public List<TransactionAnalyticsPoint> TopDestinations { get; set; } = [];
    public List<TransactionTypeSnapshot> TypeBreakdown { get; set; } = [];
    public List<TransactionAnalyticsListItem> RecentTransactions { get; set; } = [];
}

public sealed class TransactionReviewDialogModel
{
    public UncategorizedTransaction Transaction { get; set; } = new();
    public ClassificationResult? Classification { get; set; }
    public SelectionValues Selections { get; set; } = new();
}

public sealed class FireflyEnvelope<T>
{
    public List<T> Data { get; set; } = [];
    public FireflyMeta? Meta { get; set; }
}

public sealed class FireflyMeta
{
    public FireflyPagination? Pagination { get; set; }
}

public sealed class FireflyPagination
{
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

public sealed class FireflyNamedObject
{
    public string Id { get; set; } = string.Empty;
    public FireflyNamedAttributes Attributes { get; set; } = new();
}

public sealed class FireflyNamedAttributes
{
    public string Name { get; set; } = string.Empty;
}

public sealed class FireflyTransactionGroup
{
    public string Id { get; set; } = string.Empty;
    public FireflyTransactionGroupAttributes Attributes { get; set; } = new();
}

public sealed class FireflyTransactionGroupAttributes
{
    public List<FireflyTransactionSplit> Transactions { get; set; } = [];
}

public sealed class WebhookPayload
{
    public string? Trigger { get; set; }
    public string? Response { get; set; }
    public WebhookContent? Content { get; set; }
}

public sealed class WebhookContent
{
    public string? Id { get; set; }
    public List<FireflyTransactionSplit> Transactions { get; set; } = [];
}

public sealed class FireflyTransactionSplit
{
    [JsonPropertyName("transaction_journal_id")]
    public string TransactionJournalId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("destination_name")]
    public string? DestinationName { get; set; }

    [JsonPropertyName("source_name")]
    public string? SourceName { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("currency_code")]
    public string? CurrencyCode { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("budget_name")]
    public string? BudgetName { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
}