using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Tests;

public sealed class PdfRenderServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReportId = Guid.NewGuid();

    private static ReportNarrativeDto MakeNarrative() => new(
        ExecutiveSummary: "The property generated $12,500 gross revenue with 15 bookings and 66.7% occupancy.",
        RevenueNarrative: "Total gross revenue was $12,500. Airbnb contributed $9,000 across 10 bookings.",
        GuestFeedbackNarrative: "Average rating was 4.80 out of 5 across 14 reviews.",
        OperationalNarrative: "7 of 8 scheduled tasks were completed during the quarter.",
        IssuesAndResolutions: "3 issues found during inspection, all resolved.",
        Recommendations: "Consider increasing rates given 66.7% occupancy.",
        ManagerNote: "The property is performing well.");

    private static ReportContextDto MakeContext() => new(
        Property: new PropertyMetadataDto(
            Id: Guid.NewGuid(),
            Name: "Sunset Villa",
            AddressLine1: "123 Beach Rd",
            City: "Miami",
            State: "FL",
            PostalCode: "33101",
            CountryCode: "US",
            Units: 1,
            OwnerName: "Jane Owner",
            Timezone: "America/New_York",
            CurrencyCode: "USD"),
        Revenue: new RevenueSummaryDto(
            Total: 12500m,
            OccupancyRate: 66.7m,
            Adr: 208.33m,
            RevPar: 138.95m,
            ByMonth: new List<MonthlyRevenueDto>
            {
                new(2025, 1, 3500m, 2800m, 18),
                new(2025, 2, 4200m, 3360m, 22),
                new(2025, 3, 4800m, 3840m, 20),
            },
            ByPlatform: new List<PlatformRevenueDto>
            {
                new("Airbnb", 9000m, 7200m, 10, 42),
                new("VRBO", 3500m, 2800m, 5, 18),
            },
            PriorQuarterTotal: 11000m,
            PriorQuarterDelta: 13.6m),
        Expense: new ExpenseSummaryDto(
            Total: 3200m,
            NetToOwner: 9300m,
            ByCategory: new List<CategoryExpenseDto>
            {
                new("Cleaning", 1200m, 6),
                new("Maintenance", 2000m, 3),
            },
            ByMonth: new List<MonthlyExpenseDto>
            {
                new(2025, 1, 900m, 3),
                new(2025, 2, 1100m, 4),
                new(2025, 3, 1200m, 5),
            },
            PriorQuarterTotal: 3000m,
            PriorQuarterDelta: 6.7m),
        Tasks: new TaskSummaryDto(
            Total: 8,
            CompletionRate: 87.5m,
            OnTimeRate: 85.7m,
            MissedCount: 1,
            LateCount: 0),
        Reviews: new ReviewSummaryDto(
            AverageRating: 4.80m,
            Count: 14,
            Distribution: new Dictionary<int, int>
            {
                [5] = 10,
                [4] = 3,
                [3] = 1,
            },
            RecentReviews: new List<ReviewSnippetDto>
            {
                new("Airbnb", new DateOnly(2025, 3, 15), 5.0m, "Wonderful stay, highly recommend!"),
                new("VRBO", new DateOnly(2025, 2, 20), 4.5m, "Very comfortable and clean."),
                new("Airbnb", new DateOnly(2025, 1, 10), 3.0m, "Decent but a few issues with WiFi."),
            },
            PriorQuarterAverageRating: 4.6m,
            PriorQuarterDelta: 0.2m),
        Inspections: new InspectionSummaryDto(
            Count: 1,
            AverageScore: 92.0m,
            TotalIssuesFound: 3,
            TotalIssuesResolved: 3),
        FlaggedIssues: new List<FlaggedIssueDto>());

    private static Mock<IBlobService> MakeBlobServiceMock()
    {
        Mock<IBlobService> mock = new();
        mock.Setup(b => b.UploadBlobAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(b => b.GenerateSasDownloadUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://example.blob.core.windows.net/reports/test.pdf?sas=token"));
        return mock;
    }

    [Fact]
    public async Task RenderAndUploadAsync_ValidInputs_ProducesPdfWithValidSize()
    {
        Mock<IBlobService> blobMock = MakeBlobServiceMock();
        PdfRenderService sut = new(blobMock.Object, NullLogger<PdfRenderService>.Instance);

        PdfRenderResult result = await sut.RenderAndUploadAsync(
            MakeNarrative(), MakeContext(), TenantId, ReportId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal($"tenants/{TenantId}/reports/{ReportId}.pdf", result.BlobPath);
        Assert.NotNull(result.SasDownloadUrl);
        Assert.Contains("Sunset_Villa", result.ContentDispositionFilename);
        Assert.EndsWith(".pdf", result.ContentDispositionFilename);
    }

    [Fact]
    public async Task RenderAndUploadAsync_ValidInputs_UploadedBytesAreNonZeroAndUnder5MB()
    {
        byte[]? capturedBytes = null;
        Mock<IBlobService> blobMock = new();
        blobMock.Setup(b => b.UploadBlobAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Stream, string, CancellationToken>(
                (_, _, stream, _, _) =>
                {
                    using MemoryStream ms = new();
                    stream.CopyTo(ms);
                    capturedBytes = ms.ToArray();
                })
            .Returns(Task.CompletedTask);
        blobMock.Setup(b => b.GenerateSasDownloadUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://example.blob.core.windows.net/reports/test.pdf?sas=token"));

        PdfRenderService sut = new(blobMock.Object, NullLogger<PdfRenderService>.Instance);

        await sut.RenderAndUploadAsync(
            MakeNarrative(), MakeContext(), TenantId, ReportId, CancellationToken.None);

        Assert.NotNull(capturedBytes);
        Assert.True(capturedBytes!.Length > 0, "PDF must have non-zero byte length.");
        Assert.True(capturedBytes.Length < 5 * 1024 * 1024,
            $"PDF must be under 5 MB, was {capturedBytes.Length} bytes.");
    }

    [Fact]
    public async Task RenderAndUploadAsync_BlobPathFollowsTenantReportPattern()
    {
        string? capturedContainer = null;
        string? capturedBlobPath = null;
        Mock<IBlobService> blobMock = new();
        blobMock.Setup(b => b.UploadBlobAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Stream, string, CancellationToken>(
                (container, blobPath, _, _, _) =>
                {
                    capturedContainer = container;
                    capturedBlobPath = blobPath;
                })
            .Returns(Task.CompletedTask);
        blobMock.Setup(b => b.GenerateSasDownloadUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://example.blob.core.windows.net/reports/test.pdf?sas=token"));

        PdfRenderService sut = new(blobMock.Object, NullLogger<PdfRenderService>.Instance);

        await sut.RenderAndUploadAsync(
            MakeNarrative(), MakeContext(), TenantId, ReportId, CancellationToken.None);

        Assert.Equal("reports", capturedContainer);
        Assert.Equal($"tenants/{TenantId}/reports/{ReportId}.pdf", capturedBlobPath);
    }

    [Theory]
    [InlineData("", "RevenueNarrative", "GuestFeedbackNarrative", "OperationalNarrative", "IssuesAndResolutions", "Recommendations")]
    [InlineData("ExecutiveSummary", "", "GuestFeedbackNarrative", "OperationalNarrative", "IssuesAndResolutions", "Recommendations")]
    [InlineData("ExecutiveSummary", "RevenueNarrative", "", "OperationalNarrative", "IssuesAndResolutions", "Recommendations")]
    [InlineData("ExecutiveSummary", "RevenueNarrative", "GuestFeedbackNarrative", "", "IssuesAndResolutions", "Recommendations")]
    [InlineData("ExecutiveSummary", "RevenueNarrative", "GuestFeedbackNarrative", "OperationalNarrative", "", "Recommendations")]
    [InlineData("ExecutiveSummary", "RevenueNarrative", "GuestFeedbackNarrative", "OperationalNarrative", "IssuesAndResolutions", "")]
    public async Task RenderAndUploadAsync_NullOrEmptySection_ThrowsReportRenderException(
        string execSummary, string revenueNarrative, string guestFeedback,
        string operational, string issues, string recommendations)
    {
        ReportNarrativeDto narrative = new(
            ExecutiveSummary: execSummary,
            RevenueNarrative: revenueNarrative,
            GuestFeedbackNarrative: guestFeedback,
            OperationalNarrative: operational,
            IssuesAndResolutions: issues,
            Recommendations: recommendations,
            ManagerNote: "ok");

        Mock<IBlobService> blobMock = MakeBlobServiceMock();
        PdfRenderService sut = new(blobMock.Object, NullLogger<PdfRenderService>.Instance);

        await Assert.ThrowsAsync<ReportRenderException>(() =>
            sut.RenderAndUploadAsync(narrative, MakeContext(), TenantId, ReportId, CancellationToken.None));
    }

    [Fact]
    public async Task RenderAndUploadAsync_SasUrlPassedThroughFromBlobService()
    {
        Uri expectedSas = new("https://mystorage.blob.core.windows.net/reports/x.pdf?sv=2023&sig=abc");
        Mock<IBlobService> blobMock = MakeBlobServiceMock();
        blobMock.Setup(b => b.GenerateSasDownloadUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(expectedSas);

        PdfRenderService sut = new(blobMock.Object, NullLogger<PdfRenderService>.Instance);

        PdfRenderResult result = await sut.RenderAndUploadAsync(
            MakeNarrative(), MakeContext(), TenantId, ReportId, CancellationToken.None);

        Assert.Equal(expectedSas, result.SasDownloadUrl);
    }
}
