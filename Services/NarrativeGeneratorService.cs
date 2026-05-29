using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Services;

public interface INarrativeGeneratorService
{
    Task<NarrativeGenerationResult> GenerateAsync(NarrativeReportContextDto context, CancellationToken ct);
}

public sealed record NarrativeGenerationResult(
    bool Success,
    ReportNarrativeDto? Narrative,
    string? ErrorMessage,
    string? ModelUsed,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    int LatencyMs);

public sealed class NarrativeGeneratorService(
    IHttpClientFactory httpClientFactory,
    ILogger<NarrativeGeneratorService> logger) : INarrativeGeneratorService
{
    private const string AnthropicClientName = "Anthropic";
    private const string OpenAiClientName = "OpenAi";
    private const string AnthropicModel = "claude-sonnet-4-6";
    private const string OpenAiModel = "gpt-4o";

    private const decimal AnthropicInputRatePerToken = 0.000003m;
    private const decimal AnthropicOutputRatePerToken = 0.000015m;
    private const decimal OpenAiInputRatePerToken = 0.0000025m;
    private const decimal OpenAiOutputRatePerToken = 0.00001m;

    private const decimal MaxCostUsd = 0.25m;
    private const int MaxReviewSamples = 20;
    private const int AnthropicMaxTokens = 4000;
    private const double AnthropicTemperature = 0.3;
    private const int TimeoutSeconds = 30;
    private const int RetryDelayMs = 2000;
    private const double ValidationTolerancePct = 0.01;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<string> PromptTemplate = new(() =>
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = "StrataReports.Functions.Prompts.quarterly_owner_report.txt";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public async Task<NarrativeGenerationResult> GenerateAsync(NarrativeReportContextDto context, CancellationToken ct)
    {
        long startTs = Stopwatch.GetTimestamp();

        string prompt = BuildPrompt(context);

        NarrativeGenerationResult? result = await TryAnthropicAsync(prompt, context, ct);

        if (result is null)
        {
            logger.LogWarning("Anthropic failed for report {ReportId}, falling back to OpenAI GPT-4o", context.ReportId);
            result = await TryOpenAiAsync(prompt, context, ct);
        }

        int totalMs = (int)Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;

        if (result is null)
        {
            return new NarrativeGenerationResult(
                Success: false,
                Narrative: null,
                ErrorMessage: "All LLM providers failed to generate narrative",
                ModelUsed: null,
                InputTokens: 0,
                OutputTokens: 0,
                CostUsd: 0m,
                LatencyMs: totalMs);
        }

        return result with { LatencyMs = totalMs };
    }

    private async Task<NarrativeGenerationResult?> TryAnthropicAsync(
        string prompt, NarrativeReportContextDto context, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelayMs, ct);

            try
            {
                (string rawJson, int inputTokens, int outputTokens) = await CallAnthropicAsync(prompt, ct);
                ReportNarrativeDto? narrative = ParseNarrative(rawJson);
                if (narrative is null)
                {
                    logger.LogWarning("Anthropic attempt {Attempt} returned unparseable JSON for report {ReportId}",
                        attempt + 1, context.ReportId);
                    continue;
                }

                if (!ValidateNarrative(narrative, context))
                {
                    logger.LogWarning("Anthropic attempt {Attempt} failed numeric validation for report {ReportId}",
                        attempt + 1, context.ReportId);
                    if (attempt == 1)
                    {
                        return new NarrativeGenerationResult(
                            Success: false,
                            Narrative: null,
                            ErrorMessage: "Narrative validation failed: hallucinated numbers detected",
                            ModelUsed: AnthropicModel,
                            InputTokens: inputTokens,
                            OutputTokens: outputTokens,
                            CostUsd: ComputeCost(inputTokens, outputTokens, AnthropicInputRatePerToken, AnthropicOutputRatePerToken),
                            LatencyMs: 0);
                    }
                    continue;
                }

                decimal cost = ComputeCost(inputTokens, outputTokens, AnthropicInputRatePerToken, AnthropicOutputRatePerToken);
                return new NarrativeGenerationResult(
                    Success: true,
                    Narrative: narrative,
                    ErrorMessage: null,
                    ModelUsed: AnthropicModel,
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    CostUsd: cost,
                    LatencyMs: 0);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Anthropic attempt {Attempt} timed out for report {ReportId}",
                    attempt + 1, context.ReportId);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Anthropic attempt {Attempt} HTTP error for report {ReportId}",
                    attempt + 1, context.ReportId);
            }
        }

        return null;
    }

    private async Task<NarrativeGenerationResult?> TryOpenAiAsync(
        string prompt, NarrativeReportContextDto context, CancellationToken ct)
    {
        try
        {
            (string rawJson, int inputTokens, int outputTokens) = await CallOpenAiAsync(prompt, ct);
            ReportNarrativeDto? narrative = ParseNarrative(rawJson);
            if (narrative is null)
            {
                logger.LogWarning("OpenAI returned unparseable JSON for report {ReportId}", context.ReportId);
                return null;
            }

            if (!ValidateNarrative(narrative, context))
            {
                logger.LogWarning("OpenAI failed numeric validation for report {ReportId}", context.ReportId);
                return new NarrativeGenerationResult(
                    Success: false,
                    Narrative: null,
                    ErrorMessage: "Narrative validation failed: hallucinated numbers detected",
                    ModelUsed: OpenAiModel,
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    CostUsd: ComputeCost(inputTokens, outputTokens, OpenAiInputRatePerToken, OpenAiOutputRatePerToken),
                    LatencyMs: 0);
            }

            decimal cost = ComputeCost(inputTokens, outputTokens, OpenAiInputRatePerToken, OpenAiOutputRatePerToken);
            return new NarrativeGenerationResult(
                Success: true,
                Narrative: narrative,
                ErrorMessage: null,
                ModelUsed: OpenAiModel,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CostUsd: cost,
                LatencyMs: 0);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI timed out for report {ReportId}", context.ReportId);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OpenAI HTTP error for report {ReportId}", context.ReportId);
        }

        return null;
    }

    private async Task<(string RawJson, int InputTokens, int OutputTokens)> CallAnthropicAsync(
        string prompt, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient(AnthropicClientName);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        object requestBody = new
        {
            model = AnthropicModel,
            max_tokens = AnthropicMaxTokens,
            temperature = AnthropicTemperature,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
        };

        string requestJson = JsonSerializer.Serialize(requestBody);
        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("messages", content, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }

        string responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        string text = root
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        int inputTokens = root.TryGetProperty("usage", out JsonElement usage)
            ? usage.TryGetProperty("input_tokens", out JsonElement it) ? it.GetInt32() : 0
            : 0;

        int outputTokens = usage.ValueKind != JsonValueKind.Undefined
            ? usage.TryGetProperty("output_tokens", out JsonElement ot) ? ot.GetInt32() : 0
            : 0;

        return (text, inputTokens, outputTokens);
    }

    private async Task<(string RawJson, int InputTokens, int OutputTokens)> CallOpenAiAsync(
        string prompt, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient(OpenAiClientName);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        object requestBody = new
        {
            model = OpenAiModel,
            temperature = AnthropicTemperature,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a professional property management analyst. Respond only with valid JSON matching the schema requested by the user."
                },
                new { role = "user", content = prompt }
            },
        };

        string requestJson = JsonSerializer.Serialize(requestBody);
        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("chat/completions", content, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cts.Token);
            throw new HttpRequestException(
                $"OpenAI API returned {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }

        string responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        string text = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        int inputTokens = root.TryGetProperty("usage", out JsonElement usage)
            ? usage.TryGetProperty("prompt_tokens", out JsonElement pt) ? pt.GetInt32() : 0
            : 0;

        int outputTokens = usage.ValueKind != JsonValueKind.Undefined
            ? usage.TryGetProperty("completion_tokens", out JsonElement ct2) ? ct2.GetInt32() : 0
            : 0;

        return (text, inputTokens, outputTokens);
    }

    private static ReportNarrativeDto? ParseNarrative(string rawJson)
    {
        string json = ExtractJsonObject(rawJson);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ReportNarrativeDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
            return string.Empty;
        return raw[start..(end + 1)];
    }

    private static bool ValidateNarrative(ReportNarrativeDto narrative, NarrativeReportContextDto context)
    {
        HashSet<decimal> allowedNumbers = BuildAllowedNumbers(context);

        string fullText = string.Join(" ",
            narrative.ExecutiveSummary,
            narrative.RevenueNarrative,
            narrative.GuestFeedbackNarrative,
            narrative.OperationalNarrative,
            narrative.IssuesAndResolutions,
            narrative.Recommendations,
            narrative.ManagerNote);

        IEnumerable<decimal> mentioned = ExtractNumbers(fullText);

        foreach (decimal number in mentioned)
        {
            if (!IsAllowed(number, allowedNumbers))
                return false;
        }

        return true;
    }

    private static HashSet<decimal> BuildAllowedNumbers(NarrativeReportContextDto ctx)
    {
        HashSet<decimal> set = new();

        set.Add(ctx.TotalGrossRevenue);
        set.Add(ctx.TotalNetRevenue);
        set.Add(ctx.TotalBookings);
        set.Add(ctx.TotalNights);
        set.Add(ctx.AverageNightlyRate);
        set.Add(ctx.OccupancyRate);
        set.Add(ctx.TotalExpenses);
        set.Add(ctx.AverageGuestRating);
        set.Add(ctx.TotalReviews);
        set.Add(ctx.TasksScheduled);
        set.Add(ctx.TasksCompleted);

        foreach (NarrativeRevenueByPlatformDto p in ctx.RevenueByPlatform)
        {
            set.Add(p.GrossRevenue);
            set.Add(p.Bookings);
        }

        foreach (NarrativeExpenseByCategoryDto e in ctx.ExpensesByCategory)
            set.Add(e.Amount);

        foreach (NarrativeReviewSummaryDto r in ctx.ReviewSamples)
            set.Add(r.Rating);

        foreach (NarrativeInspectionSummaryDto i in ctx.Inspections)
        {
            if (i.Score.HasValue) set.Add(i.Score.Value);
            if (i.IssuesFound.HasValue) set.Add(i.IssuesFound.Value);
            if (i.IssuesResolved.HasValue) set.Add(i.IssuesResolved.Value);
        }

        set.Add(ctx.PeriodStart.Year);
        set.Add(ctx.PeriodEnd.Year);

        return set;
    }

    private static bool IsAllowed(decimal number, HashSet<decimal> allowed)
    {
        if (number <= 5 && number >= 1 && number == Math.Floor(number))
            return true;

        if (number >= 2000 && number <= 2030 && number == Math.Floor(number))
            return true;

        foreach (decimal a in allowed)
        {
            if (a == 0 && number == 0) return true;
            if (a == 0) continue;
            double diff = Math.Abs((double)(number - a)) / Math.Abs((double)a);
            if (diff <= ValidationTolerancePct)
                return true;
        }

        return false;
    }

    private static IEnumerable<decimal> ExtractNumbers(string text)
    {
        MatchCollection matches = Regex.Matches(text, @"\$?[\d,]+(?:\.\d+)?%?");
        foreach (Match match in matches)
        {
            string cleaned = match.Value.Replace("$", "").Replace(",", "").Replace("%", "");
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
                yield return value;
        }
    }

    private string BuildPrompt(NarrativeReportContextDto context)
    {
        IReadOnlyList<NarrativeReviewSummaryDto> reviews = TruncateReviewsIfNeeded(context);

        bool truncated = reviews.Count < context.ReviewSamples.Count;
        if (truncated)
        {
            logger.LogWarning(
                "Cost ceiling: truncated review_text list from {Original} to {Truncated} samples for report {ReportId}",
                context.ReviewSamples.Count, reviews.Count, context.ReportId);
        }

        string template = PromptTemplate.Value;

        string revenueByPlatform = context.RevenueByPlatform.Count > 0
            ? string.Join("\n", context.RevenueByPlatform.Select(p =>
                $"  {p.Platform}: ${p.GrossRevenue:F2} ({p.Bookings} bookings)"))
            : "  No platform breakdown available.";

        string expensesByCategory = context.ExpensesByCategory.Count > 0
            ? string.Join("\n", context.ExpensesByCategory.Select(e =>
                $"  {e.Category}: ${e.Amount:F2}"))
            : "  No expense records available.";

        string reviewSamples = reviews.Count > 0
            ? string.Join("\n", reviews.Select(r =>
                $"  [{r.Platform} {r.ReviewDate:yyyy-MM-dd}] Rating: {r.Rating}/5" +
                (string.IsNullOrWhiteSpace(r.ReviewText) ? "" : $" — \"{r.ReviewText}\"")))
            : "  No reviews available.";

        string inspections = context.Inspections.Count > 0
            ? string.Join("\n", context.Inspections.Select(i =>
                $"  {i.InspectionDate:yyyy-MM-dd}: Score={i.Score?.ToString("F1") ?? "N/A"}, " +
                $"Issues found={i.IssuesFound ?? 0}, Resolved={i.IssuesResolved ?? 0}" +
                (string.IsNullOrWhiteSpace(i.Notes) ? "" : $" — {i.Notes}")))
            : "  No inspections recorded.";

        return template
            .Replace("{{property_name}}", context.PropertyName)
            .Replace("{{owner_name}}", context.OwnerName ?? "Valued Owner")
            .Replace("{{period_start}}", context.PeriodStart.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture))
            .Replace("{{period_end}}", context.PeriodEnd.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture))
            .Replace("{{total_gross_revenue}}", context.TotalGrossRevenue.ToString("F2", CultureInfo.InvariantCulture))
            .Replace("{{total_net_revenue}}", context.TotalNetRevenue.ToString("F2", CultureInfo.InvariantCulture))
            .Replace("{{total_bookings}}", context.TotalBookings.ToString(CultureInfo.InvariantCulture))
            .Replace("{{total_nights}}", context.TotalNights.ToString(CultureInfo.InvariantCulture))
            .Replace("{{average_nightly_rate}}", context.AverageNightlyRate.ToString("F2", CultureInfo.InvariantCulture))
            .Replace("{{occupancy_rate}}", context.OccupancyRate.ToString("F1", CultureInfo.InvariantCulture))
            .Replace("{{revenue_by_platform}}", revenueByPlatform)
            .Replace("{{total_expenses}}", context.TotalExpenses.ToString("F2", CultureInfo.InvariantCulture))
            .Replace("{{expenses_by_category}}", expensesByCategory)
            .Replace("{{average_guest_rating}}", context.AverageGuestRating.ToString("F2", CultureInfo.InvariantCulture))
            .Replace("{{total_reviews}}", context.TotalReviews.ToString(CultureInfo.InvariantCulture))
            .Replace("{{review_samples}}", reviewSamples)
            .Replace("{{tasks_scheduled}}", context.TasksScheduled.ToString(CultureInfo.InvariantCulture))
            .Replace("{{tasks_completed}}", context.TasksCompleted.ToString(CultureInfo.InvariantCulture))
            .Replace("{{inspections}}", inspections);
    }

    private IReadOnlyList<NarrativeReviewSummaryDto> TruncateReviewsIfNeeded(NarrativeReportContextDto context)
    {
        int estimatedTokensPerReview = 50;
        int baseTokenEstimate = 800;

        int maxReviewsForBudget = (int)((MaxCostUsd / AnthropicInputRatePerToken - baseTokenEstimate) / estimatedTokensPerReview);
        int limit = Math.Min(MaxReviewSamples, maxReviewsForBudget);
        limit = Math.Max(0, limit);

        if (context.ReviewSamples.Count <= limit)
            return context.ReviewSamples;

        return context.ReviewSamples.Take(limit).ToList();
    }

    private static decimal ComputeCost(int inputTokens, int outputTokens, decimal inputRate, decimal outputRate)
        => inputTokens * inputRate + outputTokens * outputRate;
}
