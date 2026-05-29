using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using StrataReports.Functions.Models;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Tests;

public sealed class NarrativeGeneratorServiceTests
{
    private static readonly NarrativeReportContextDto SampleContext = new(
        ReportId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        PropertyId: Guid.NewGuid(),
        PropertyName: "Sunset Villa",
        OwnerName: "Jane Owner",
        PeriodStart: new DateOnly(2025, 1, 1),
        PeriodEnd: new DateOnly(2025, 3, 31),
        TotalGrossRevenue: 12500.00m,
        TotalNetRevenue: 10000.00m,
        TotalBookings: 15,
        TotalNights: 60,
        AverageNightlyRate: 208.33m,
        OccupancyRate: 66.7m,
        RevenueByPlatform: new List<NarrativeRevenueByPlatformDto>
        {
            new("Airbnb", 9000.00m, 10),
            new("VRBO", 3500.00m, 5),
        },
        TotalExpenses: 3200.00m,
        ExpensesByCategory: new List<NarrativeExpenseByCategoryDto>
        {
            new("Cleaning", 1200.00m),
            new("Maintenance", 2000.00m),
        },
        AverageGuestRating: 4.80m,
        TotalReviews: 14,
        ReviewSamples: new List<NarrativeReviewSummaryDto>
        {
            new("Airbnb", new DateOnly(2025, 2, 10), 5.0m, "Wonderful stay!"),
            new("VRBO", new DateOnly(2025, 3, 5), 4.5m, "Very comfortable."),
        },
        TasksScheduled: 8,
        TasksCompleted: 7,
        Inspections: new List<NarrativeInspectionSummaryDto>
        {
            new(new DateOnly(2025, 1, 15), 92.0m, 3, 3, "All resolved"),
        });

    private static ReportNarrativeDto MakeValidNarrative() => new(
        ExecutiveSummary: "The property generated $12,500.00 gross revenue with 15 bookings and 66.7% occupancy.",
        RevenueNarrative: "Total gross revenue was $12500.00. Airbnb contributed $9000.00 across 10 bookings.",
        GuestFeedbackNarrative: "Average rating was 4.80 out of 5 across 14 reviews.",
        OperationalNarrative: "7 of 8 scheduled tasks were completed during the quarter.",
        IssuesAndResolutions: "3 issues found during inspection on 2025-01-15, all 3 resolved.",
        Recommendations: "Consider increasing rates given 66.7% occupancy.",
        ManagerNote: "The property is performing well. Net revenue was $10000.00.");

    private static ReportNarrativeDto MakeHallucinatedNarrative() => new(
        ExecutiveSummary: "The property generated $99999.99 in revenue.",
        RevenueNarrative: "We saw incredible growth of $500000.00 this quarter.",
        GuestFeedbackNarrative: "Ratings averaged 6.5 out of 5.",
        OperationalNarrative: "999 tasks were completed.",
        IssuesAndResolutions: "None.",
        Recommendations: "Keep going.",
        ManagerNote: "All is well.");

    private static string AnthropicResponse(ReportNarrativeDto narrative) =>
        JsonSerializer.Serialize(new
        {
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(narrative, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }) }
            },
            usage = new { input_tokens = 800, output_tokens = 400 },
        });

    private static string OpenAiResponse(ReportNarrativeDto narrative) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = JsonSerializer.Serialize(narrative, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                        })
                    }
                }
            },
            usage = new { prompt_tokens = 800, completion_tokens = 400 },
        });

    private static IHttpClientFactory BuildFactory(
        Mock<HttpMessageHandler> anthropicHandler,
        Mock<HttpMessageHandler>? openAiHandler = null)
    {
        HttpClient anthropicClient = new(anthropicHandler.Object)
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/"),
        };
        anthropicClient.DefaultRequestHeaders.Add("x-api-key", "test-key");
        anthropicClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        HttpClient openAiClient = new(openAiHandler?.Object ?? new Mock<HttpMessageHandler>().Object)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
        };

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient("Anthropic")).Returns(anthropicClient);
        factory.Setup(f => f.CreateClient("OpenAi")).Returns(openAiClient);
        return factory.Object;
    }

    [Fact]
    public async Task SuccessfulPath_AnthropicReturnsValidNarrative_ReturnsSuccess()
    {
        ReportNarrativeDto narrative = MakeValidNarrative();
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AnthropicResponse(narrative), Encoding.UTF8, "application/json"),
            });

        NarrativeGeneratorService sut = new(BuildFactory(handler), NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(SampleContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Narrative);
        Assert.True(result.ModelUsed!.Contains("claude", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(800, result.InputTokens);
        Assert.Equal(400, result.OutputTokens);
        Assert.True(result.CostUsd > 0);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ValidationFailure_FirstAttemptFails_SecondAttemptSucceeds_ReturnsSuccess()
    {
        ReportNarrativeDto hallucinated = MakeHallucinatedNarrative();
        ReportNarrativeDto valid = MakeValidNarrative();

        int callCount = 0;
        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                ReportNarrativeDto body = callCount == 1 ? hallucinated : valid;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AnthropicResponse(body), Encoding.UTF8, "application/json"),
                };
            });

        NarrativeGeneratorService sut = new(BuildFactory(handler), NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(SampleContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Narrative);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ValidationFailure_BothAttemptsFail_ReturnsFailedWithHallucinationMessage()
    {
        ReportNarrativeDto hallucinated = MakeHallucinatedNarrative();

        Mock<HttpMessageHandler> anthropicHandler = new();
        anthropicHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken __) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AnthropicResponse(hallucinated), Encoding.UTF8, "application/json"),
                }));

        Mock<HttpMessageHandler> openAiHandler = new();
        openAiHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken __) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(OpenAiResponse(hallucinated), Encoding.UTF8, "application/json"),
                }));

        NarrativeGeneratorService sut = new(
            BuildFactory(anthropicHandler, openAiHandler),
            NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(SampleContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("hallucinated numbers", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnthropicTimeout_FallsBackToOpenAi_ReturnsSuccess()
    {
        ReportNarrativeDto valid = MakeValidNarrative();

        Mock<HttpMessageHandler> anthropicHandler = new();
        anthropicHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        Mock<HttpMessageHandler> openAiHandler = new();
        openAiHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OpenAiResponse(valid), Encoding.UTF8, "application/json"),
            });

        NarrativeGeneratorService sut = new(
            BuildFactory(anthropicHandler, openAiHandler),
            NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(SampleContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Narrative);
        Assert.Equal("gpt-4o", result.ModelUsed, ignoreCase: true);
    }

    [Fact]
    public async Task AnthropicAndOpenAiBothFail_ReturnsFailure()
    {
        Mock<HttpMessageHandler> anthropicHandler = new();
        anthropicHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken __) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"server error\"}", Encoding.UTF8, "application/json"),
                }));

        Mock<HttpMessageHandler> openAiHandler = new();
        openAiHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken __) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"unavailable\"}", Encoding.UTF8, "application/json"),
                }));

        NarrativeGeneratorService sut = new(
            BuildFactory(anthropicHandler, openAiHandler),
            NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(SampleContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CostCeilingTruncation_ManyReviews_TruncatesAndLogs()
    {
        List<NarrativeReviewSummaryDto> manyReviews = Enumerable.Range(0, 30)
            .Select(i => new NarrativeReviewSummaryDto(
                "Airbnb",
                new DateOnly(2025, 1, i % 28 + 1),
                4.5m,
                $"Review number {i} was a great stay with excellent service and amenities."))
            .ToList();

        NarrativeReportContextDto contextWithManyReviews = SampleContext with
        {
            ReviewSamples = manyReviews,
        };

        ReportNarrativeDto narrative = MakeValidNarrative();
        string? capturedRequestBody = null;

        Mock<HttpMessageHandler> handler = new();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken _) =>
            {
                capturedRequestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AnthropicResponse(narrative), Encoding.UTF8, "application/json"),
                });
            });

        NarrativeGeneratorService sut = new(BuildFactory(handler), NullLogger<NarrativeGeneratorService>.Instance);

        NarrativeGenerationResult result = await sut.GenerateAsync(contextWithManyReviews, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedRequestBody);

        int reviewCount = CountOccurrences(capturedRequestBody!, "Review number");
        Assert.True(reviewCount <= 20, $"Expected at most 20 reviews in prompt, got {reviewCount}");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
