using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FireflyCategorizer.DotNet.Models;

namespace FireflyCategorizer.DotNet.Services;

public sealed class AiClassifierService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiClassifierService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AiClassifierService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<AiClassifierService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClassificationResult?> ClassifyAsync(
        IReadOnlyList<string> categories,
        IReadOnlyList<string> budgets,
        string destinationName,
        string description,
        IReadOnlyList<RecentTransaction> recentTransactions,
        CancellationToken cancellationToken)
    {
        if (HasMistral())
        {
            return await ClassifyWithMistralAsync(categories, budgets, destinationName, description, recentTransactions, cancellationToken);
        }

        if (HasOpenAi())
        {
            return await ClassifyWithOpenAiAsync(categories, budgets, destinationName, description, recentTransactions, cancellationToken);
        }

        throw new InvalidOperationException("No AI provider is configured. Set MISTRAL_API_KEY or OPENAI_API_KEY.");
    }

    public async Task<object> DiagnoseAsync(CancellationToken cancellationToken)
    {
        if (HasMistral())
        {
            return await DiagnoseProviderAsync(
                providerName: "mistral",
                url: "https://api.mistral.ai/v1/models",
                apiKey: GetRequired("MISTRAL_API_KEY"),
                configuredModel: _configuration["MISTRAL_MODEL"] ?? "mistral-small-latest",
                cancellationToken: cancellationToken);
        }

        if (HasOpenAi())
        {
            return await DiagnoseProviderAsync(
                providerName: "openai",
                url: "https://api.openai.com/v1/models",
                apiKey: GetRequired("OPENAI_API_KEY"),
                configuredModel: _configuration["OPENAI_MODEL"] ?? "gpt-4o-mini",
                cancellationToken: cancellationToken);
        }

        throw new InvalidOperationException("No AI provider is configured. Set MISTRAL_API_KEY or OPENAI_API_KEY.");
    }

    private bool HasMistral() => !string.IsNullOrWhiteSpace(_configuration["MISTRAL_API_KEY"]);

    private bool HasOpenAi() => !string.IsNullOrWhiteSpace(_configuration["OPENAI_API_KEY"]);

    private async Task<ClassificationResult?> ClassifyWithMistralAsync(
        IReadOnlyList<string> categories,
        IReadOnlyList<string> budgets,
        string destinationName,
        string description,
        IReadOnlyList<RecentTransaction> recentTransactions,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(destinationName, description, recentTransactions);
        var requestBody = new
        {
            model = _configuration["MISTRAL_MODEL"] ?? "mistral-small-latest",
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { BuildToolDefinition(categories, budgets) },
            tool_choice = new { type = "function", function = new { name = "classification" } },
            parallel_tool_calls = false,
            temperature = 0,
        };

        var rawArguments = await ExecuteCompletionAsync(
            url: "https://api.mistral.ai/v1/chat/completions",
            apiKey: GetRequired("MISTRAL_API_KEY"),
            requestBody: requestBody,
            contentPath: "choices/0/message/content",
            toolPath: "choices/0/message/tool_calls/0/function/arguments",
            cancellationToken: cancellationToken);

        return BuildClassificationResult(prompt, rawArguments, categories, budgets);
    }

    private async Task<ClassificationResult?> ClassifyWithOpenAiAsync(
        IReadOnlyList<string> categories,
        IReadOnlyList<string> budgets,
        string destinationName,
        string description,
        IReadOnlyList<RecentTransaction> recentTransactions,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(destinationName, description, recentTransactions);
        var requestBody = new
        {
            model = _configuration["OPENAI_MODEL"] ?? "gpt-4o-mini",
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { BuildToolDefinition(categories, budgets) },
            tool_choice = new { type = "function", function = new { name = "classification" } },
        };

        var rawArguments = await ExecuteCompletionAsync(
            url: "https://api.openai.com/v1/chat/completions",
            apiKey: GetRequired("OPENAI_API_KEY"),
            requestBody: requestBody,
            contentPath: "choices/0/message/content",
            toolPath: "choices/0/message/tool_calls/0/function/arguments",
            cancellationToken: cancellationToken);

        return BuildClassificationResult(prompt, rawArguments, categories, budgets);
    }

    private async Task<object> DiagnoseProviderAsync(string providerName, string url, string apiKey, string configuredModel, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var availableModels = document.RootElement.TryGetProperty("data", out var data)
            ? data.EnumerateArray().Select(item => item.GetProperty("id").GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
            : [];

        return new
        {
            ok = true,
            provider = providerName,
            model = configuredModel,
            modelAvailable = availableModels.Contains(configuredModel),
            availableModelCount = availableModels.Count,
        };
    }

    private async Task<string?> ExecuteCompletionAsync(string url, string apiKey, object requestBody, string contentPath, string toolPath, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI request failed: {(int)response.StatusCode} - {body}");
        }

        using var document = JsonDocument.Parse(body);
        var rawArguments = TryGetStringByPath(document.RootElement, toolPath) ?? ExtractJsonFromContent(TryGetElementByPath(document.RootElement, contentPath));

        return rawArguments;
    }

    private ClassificationResult? BuildClassificationResult(string prompt, string? rawArguments, IReadOnlyList<string> categories, IReadOnlyList<string> budgets)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            _logger.LogWarning("AI response did not contain a tool call or parseable JSON content.");
            return null;
        }

        using var document = JsonDocument.Parse(rawArguments);
        var root = document.RootElement;
        var categoryGuess = root.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null;
        var budgetGuess = root.TryGetProperty("budget", out var budgetElement) ? budgetElement.GetString() : null;

        var category = categories.Contains(categoryGuess) ? categoryGuess : null;
        var budget = budgets.Contains(budgetGuess) ? budgetGuess : null;

        if (category is null && budget is null)
        {
            _logger.LogWarning("AI could not classify the transaction. Guess: {Arguments}", rawArguments);
            return null;
        }

        return new ClassificationResult
        {
            Prompt = prompt,
            Response = rawArguments,
            Category = category,
            Budget = budget,
        };
    }

    private static object BuildToolDefinition(IReadOnlyList<string> categories, IReadOnlyList<string> budgets)
    {
        return new
        {
            type = "function",
            function = new
            {
                name = "classification",
                description = "Classify a financial transaction into a category and budget, use only values from the lists provided.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new
                        {
                            type = "string",
                            description = $"The category to classify the transaction into. Use only these values: {string.Join(", ", categories)}. Use none if no category applies."
                        },
                        budget = new
                        {
                            type = "string",
                            description = $"The budget to classify the transaction into. Use only these values: {string.Join(", ", budgets)}. Use none if no budget applies."
                        }
                    },
                    required = new[] { "category", "budget" }
                }
            }
        };
    }

    private static string BuildPrompt(string destinationName, string description, IReadOnlyList<RecentTransaction> recentTransactions)
    {
        var historyBlock = recentTransactions.Count > 0
            ? $"Recent categorized transactions for the same destination:{Environment.NewLine}{string.Join(Environment.NewLine, recentTransactions.Select((transaction, index) => $"{index + 1}. date={transaction.Date}; amount={transaction.Amount} {transaction.CurrencyCode ?? string.Empty}; description={transaction.Description}; category={transaction.Category ?? "none"}; budget={transaction.Budget ?? "none"}"))}{Environment.NewLine}{Environment.NewLine}Use these as hints for consistency, but prefer the current transaction details if they clearly differ.{Environment.NewLine}{Environment.NewLine}"
            : string.Empty;

        return $"Categorize this transaction from my bank account with the following description {description} and the following destination {destinationName}.{Environment.NewLine}{historyBlock}Return the result by calling the classification function. If you cannot confidently classify it, still call the function and use \"none\" for unknown values.";
    }

    private string GetRequired(string name) => _configuration[name] ?? throw new InvalidOperationException($"Missing required configuration value '{name}'.");

    private static string? TryGetStringByPath(JsonElement root, string path)
    {
        var element = TryGetElementByPath(root, path);
        return element is { Found: true, Value.ValueKind: JsonValueKind.String } ? element.Value.GetString() : null;
    }

    private static (bool Found, JsonElement Value) TryGetElementByPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
                {
                    return (false, default);
                }

                current = current[index];
                continue;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return (false, default);
            }
        }

        return (true, current);
    }

    private static string? ExtractJsonFromContent((bool Found, JsonElement Value) contentResult)
    {
        if (!contentResult.Found)
        {
            return null;
        }

        var content = contentResult.Value;
        string? text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(part => part.TryGetProperty("text", out var textPart) ? textPart.GetString() : part.GetString()).Where(value => !string.IsNullOrWhiteSpace(value))),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }
}