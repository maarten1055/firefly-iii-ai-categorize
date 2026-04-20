using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;
using FireflyCategorizer.Models;

namespace FireflyCategorizer.Services;

public sealed partial class FireflyService
{
    private const int UncategorizedPageSize = 100;
    private const int AnalyticsPageSize = 1000;
    private static readonly TimeSpan UncategorizedCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly (string Id, string Query)[] UncategorizedSources =
    [
        ("missing-category", "has_any_category:false && type:withdrawal"),
        ("missing-budget", "has_any_budget:false && type:withdrawal"),
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FireflyService> _logger;
    private readonly SemaphoreSlim _scanStateLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private UncategorizedCache? _uncategorizedCache;
    private Task? _uncategorizedScanTask;
    private Task? _uncategorizedBackgroundScanTask;

    public FireflyService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<FireflyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await GetNamedMapAsync("/api/v1/categories", cancellationToken);
    }

    public async Task<Dictionary<string, string>> GetBudgetsAsync(CancellationToken cancellationToken = default)
    {
        return await GetNamedMapAsync("/api/v1/budgets", cancellationToken);
    }

    public async Task<Dictionary<string, string>> GetBillsAsync(CancellationToken cancellationToken = default)
    {
        return await GetNamedMapAsync("/api/v1/bills", cancellationToken);
    }

    public async Task SetCategoryAndBudgetAsync(string transactionId, IReadOnlyList<FireflyTransactionSplit> transactions, string? categoryId, string? budgetId, CancellationToken cancellationToken = default)
    {
        var tag = _configuration["FIREFLY_TAG"] ?? "AI categorized";
        var body = new
        {
            apply_rules = false,
            fire_webhooks = false,
            transactions = transactions.Select(transaction =>
            {
                var tags = transaction.Tags.Append(tag).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
                var payload = new Dictionary<string, object?>
                {
                    ["transaction_journal_id"] = transaction.TransactionJournalId,
                    ["tags"] = tags,
                };

                if (!string.IsNullOrWhiteSpace(categoryId))
                {
                    payload["category_id"] = categoryId;
                }

                if (!string.IsNullOrWhiteSpace(budgetId))
                {
                    payload["budget_id"] = budgetId;
                }

                return payload;
            }).ToArray()
        };

        await AuthorizedSendAsync(HttpMethod.Put, $"/api/v1/transactions/{transactionId}", body, cancellationToken);
        InvalidateUncategorizedCache();
        _logger.LogInformation("Transaction {TransactionId} updated in Firefly III.", transactionId);
    }

    public async Task<object> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var categories = await GetCategoriesAsync(cancellationToken);
        var budgets = await GetBudgetsAsync(cancellationToken);

        return new
        {
            ok = true,
            baseUrl = BaseUrl,
            categories = categories.Count,
            budgets = budgets.Count,
        };
    }

    private async Task<Dictionary<string, string>> GetNamedMapAsync(string path, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var page = 1;

        while (true)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var envelope = await AuthorizedGetAsync<FireflyEnvelope<FireflyNamedObject>>($"{path}{separator}page={page}", cancellationToken)
                ?? new FireflyEnvelope<FireflyNamedObject>();

            foreach (var item in envelope.Data)
            {
                if (!string.IsNullOrWhiteSpace(item.Attributes.Name))
                {
                    result[item.Attributes.Name] = item.Id;
                }
            }

            if (envelope.Data.Count == 0 || envelope.Meta?.Pagination is null || page >= envelope.Meta.Pagination.TotalPages)
            {
                break;
            }

            page += 1;
        }

        return result;
    }

    private async Task<FireflyEnvelope<FireflyTransactionGroup>> GetTransactionsPageAsync(string transactionType, int page, int limit, DateTimeOffset? startDate, DateTimeOffset? endDate, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["type"] = transactionType,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        };

        if (startDate.HasValue)
        {
            parameters["start"] = startDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (endDate.HasValue)
        {
            parameters["end"] = endDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var url = $"/api/v1/transactions?{string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
        return await AuthorizedGetAsync<FireflyEnvelope<FireflyTransactionGroup>>(url, cancellationToken)
            ?? new FireflyEnvelope<FireflyTransactionGroup>();
    }

    private async Task<FireflyEnvelope<FireflyTransactionGroup>> GetSearchTransactionsAsync(string query, int page, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["query"] = query,
            ["page"] = page.ToString(),
            ["limit"] = limit.ToString(),
        };
        var url = $"/api/v1/search/transactions?{string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
        return await AuthorizedGetAsync<FireflyEnvelope<FireflyTransactionGroup>>(url, cancellationToken)
            ?? new FireflyEnvelope<FireflyTransactionGroup>();
    }

    private async Task<T?> AuthorizedGetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequired("FIREFLY_PERSONAL_TOKEN"));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Error while communicating with Firefly III at {request.RequestUri}: {(int)response.StatusCode} - {body}");
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private async Task AuthorizedSendAsync(HttpMethod method, string relativeUrl, object body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, BuildUrl(relativeUrl))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequired("FIREFLY_PERSONAL_TOKEN"));

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Error while communicating with Firefly III at {request.RequestUri}: {(int)response.StatusCode} - {responseBody}");
        }
    }

    private string BuildUrl(string relativeUrl) => $"{BaseUrl}{relativeUrl}";

    private string BaseUrl
    {
        get
        {
            var url = GetRequired("FIREFLY_URL").TrimEnd('/');
            return url;
        }
    }

    private string GetRequired(string name) => _configuration[name] ?? throw new InvalidOperationException($"Missing required configuration value '{name}'.");

    private static string EscapeSearchValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static DateTimeOffset ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static decimal ParseAmount(string? value)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return decimal.Abs(parsed);
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
        {
            return decimal.Abs(parsed);
        }

        return 0m;
    }

    private static string GetCounterpartyLabel(AnalyticsTransactionRow row)
    {
        var candidate = row.Type == "deposit" ? row.SourceName : row.DestinationName;
        return string.IsNullOrWhiteSpace(candidate) ? "Unknown counterparty" : candidate;
    }

}