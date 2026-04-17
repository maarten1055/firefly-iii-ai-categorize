using FireflyCategorizer.DotNet.Models;
using FireflyCategorizer.DotNet.Services;
using Microsoft.FluentUI.AspNetCore.Components;

LoadDotEnv(
	Directory.GetCurrentDirectory(),
	Path.Combine(Directory.GetCurrentDirectory(), "FireflyCategorizer.DotNet"),
	AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

LoadDotEnv(builder.Environment.ContentRootPath, Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.ConfigureHttpJsonOptions(options =>
{
	options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});
builder.Services.AddHttpClient();
builder.Services.AddSingleton<FireflyService>();
builder.Services.AddSingleton<AiClassifierService>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
builder.Services.AddSingleton<CategorizationCoordinator>();

var app = builder.Build();

var enableUi = string.Equals(app.Configuration["ENABLE_UI"], "true", StringComparison.OrdinalIgnoreCase);

app.UseStaticFiles();
app.UseAntiforgery();

if (enableUi)
{
	app.MapGet("/index.html", () => Results.Redirect("/", permanent: false));
	app.MapGet("/uncategorized.html", () => Results.Redirect("/uncategorized", permanent: false));
	app.MapRazorComponents<FireflyCategorizer.DotNet.Components.App>()
		.AddInteractiveServerRenderMode();
}
else
{
	app.MapGet("/", () => Results.Text("Web UI is disabled. Set ENABLE_UI=true and restart the application to serve the UI at /."));
	app.MapGet("/uncategorized", () => Results.Text("Web UI is disabled. Set ENABLE_UI=true and restart the application to serve the UI at /."));
}

app.MapGet("/api/diagnostics", async (FireflyService fireflyService, AiClassifierService aiClassifierService, CancellationToken cancellationToken) =>
{
	var checks = new List<object>();

	try
	{
		checks.Add(new { name = "firefly", ok = true, details = await fireflyService.DiagnoseAsync(cancellationToken) });
	}
	catch (Exception exception)
	{
		checks.Add(new { name = "firefly", ok = false, error = exception.Message });
	}

	try
	{
		checks.Add(new { name = "ai", ok = true, details = await aiClassifierService.DiagnoseAsync(cancellationToken) });
	}
	catch (Exception exception)
	{
		checks.Add(new { name = "ai", ok = false, error = exception.Message });
	}

	var ok = checks.All(check => (bool)check.GetType().GetProperty("ok")!.GetValue(check)!);
	return Results.Json(new { ok, checks }, statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/transactions/options", async (FireflyService fireflyService, CancellationToken cancellationToken) =>
{
	try
	{
		var categories = (await fireflyService.GetCategoriesAsync(cancellationToken)).Keys.OrderBy(value => value, StringComparer.Ordinal).ToList();
		var budgets = (await fireflyService.GetBudgetsAsync(cancellationToken)).Keys.OrderBy(value => value, StringComparer.Ordinal).ToList();
		return Results.Json(new { ok = true, categories, budgets });
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 500);
	}
});

app.MapGet("/api/transactions/metadata", async (string? destination, FireflyService fireflyService, CancellationToken cancellationToken) =>
{
	try
	{
		var result = await fireflyService.GetUncategorizedMetadataAsync(destination, cancellationToken);
		return Results.Json(new { ok = true, result.Summary, result.Destinations, result.Complete });
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 500);
	}
});

app.MapGet("/api/transactions/uncategorized", async (int? page, int? limit, string? destination, FireflyService fireflyService, CancellationToken cancellationToken) =>
{
	try
	{
		var result = await fireflyService.GetUncategorizedTransactionsAsync(page ?? 1, limit ?? 20, destination, cancellationToken);
		return Results.Json(new
		{
			ok = true,
			result.Page,
			result.Limit,
			result.HasNextPage,
			result.Items,
			result.TotalTransactions,
			result.TotalPages,
			result.Complete,
		});
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 500);
	}
});

app.MapPost("/api/transactions/classify", async (ClassifyTransactionRequest request, CategorizationCoordinator coordinator, CancellationToken cancellationToken) =>
{
	try
	{
		if (request.Transaction is null)
		{
			throw new InvalidOperationException("Missing transaction payload.");
		}

		var classification = await coordinator.ClassifyForUiAsync(request.Transaction, cancellationToken);
		return Results.Json(new { ok = true, classification });
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 400);
	}
});

app.MapPost("/api/transactions/apply", async (ApplyTransactionRequest request, CategorizationCoordinator coordinator, CancellationToken cancellationToken) =>
{
	try
	{
		var result = await coordinator.ApplyTransactionUpdateAsync(request, cancellationToken);
		return Results.Json(new { ok = true, transaction = result.Transaction, status = result.Status });
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 400);
	}
});

app.MapPost("/api/transactions/apply-destination", async (ApplyDestinationRequest request, CategorizationCoordinator coordinator, CancellationToken cancellationToken) =>
{
	try
	{
		var updatedCount = await coordinator.ApplyDestinationUpdateAsync(request.Destination ?? string.Empty, request.Selections, cancellationToken);
		return Results.Json(new { ok = true, updatedCount, destination = request.Destination });
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 400);
	}
});

app.MapPost("/api/jobs/{id}/fill-missing", async (string id, CategorizationCoordinator coordinator, CancellationToken cancellationToken) =>
{
	try
	{
		var job = await coordinator.FillMissingJobValueAsync(id, cancellationToken);
		return Results.Json(new { ok = true, job });
	}
	catch (KeyNotFoundException exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 404);
	}
	catch (Exception exception)
	{
		return Results.Json(new { ok = false, error = exception.Message }, statusCode: 400);
	}
});

app.MapPost("/webhook", async (WebhookPayload payload, CategorizationCoordinator coordinator, CancellationToken cancellationToken) =>
{
	try
	{
		await coordinator.QueueWebhookAsync(payload, cancellationToken);
		return Results.Text("Queued");
	}
	catch (Exception exception)
	{
		return Results.Text(exception.Message, statusCode: 400);
	}
});

app.Run();

static void LoadDotEnv(params string[] searchRoots)
{
	var candidateFiles = searchRoots
		.Where(path => !string.IsNullOrWhiteSpace(path))
		.SelectMany(path => new[]
		{
			Path.Combine(path, ".env"),
			Path.Combine(path, "FireflyCategorizer.DotNet", ".env")
		})
		.Distinct(StringComparer.OrdinalIgnoreCase);

	foreach (var candidateFile in candidateFiles)
	{
		if (!File.Exists(candidateFile))
		{
			continue;
		}

		foreach (var rawLine in File.ReadAllLines(candidateFile))
		{
			var line = rawLine.Trim();
			if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
			{
				continue;
			}

			var separatorIndex = line.IndexOf('=');
			if (separatorIndex <= 0)
			{
				continue;
			}

			var key = line[..separatorIndex].Trim();
			if (string.IsNullOrWhiteSpace(key) || Environment.GetEnvironmentVariable(key) is not null)
			{
				continue;
			}

			var value = line[(separatorIndex + 1)..].Trim();
			if (value.Length >= 2 && ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
			{
				value = value[1..^1];
			}

			Environment.SetEnvironmentVariable(key, value);
		}
	}
}
