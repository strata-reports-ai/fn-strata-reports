using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StrataReports.Functions.Charts;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Services;

public sealed class PdfRenderService(
    IBlobService blobService,
    ILogger<PdfRenderService> logger) : IPdfRenderService
{
    private const string ReportsContainer = "reports";
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    static PdfRenderService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<PdfRenderResult> RenderAndUploadAsync(
        ReportNarrativeDto narrative,
        ReportContextDto context,
        Guid tenantId,
        Guid reportId,
        CancellationToken ct)
    {
        ValidateInputs(narrative, context);

        logger.LogInformation(
            "Rendering PDF for report {ReportId} tenant={TenantId}", reportId, tenantId);

        byte[] pdfBytes = RenderPdf(narrative, context, reportId);

        if (pdfBytes.Length > MaxFileSizeBytes)
            throw new ReportRenderException(
                $"Rendered PDF size {pdfBytes.Length} bytes exceeds 5 MB limit.");

        string blobPath = $"tenants/{tenantId}/reports/{reportId}.pdf";
        using MemoryStream ms = new(pdfBytes);
        await blobService.UploadBlobAsync(ReportsContainer, blobPath, ms, "application/pdf", ct);

        Uri sasUrl = blobService.GenerateSasDownloadUrl(ReportsContainer, blobPath, TimeSpan.FromHours(24));

        string safePropertyName = context.Property.Name.Replace(' ', '_');
        string period = FormatPeriodForFilename(context.Revenue);
        string filename = $"{safePropertyName}_{period}_Owner_Report.pdf";

        logger.LogInformation(
            "PDF uploaded to {BlobPath}, size={SizeBytes}B", blobPath, pdfBytes.Length);

        return new PdfRenderResult(blobPath, sasUrl, filename);
    }

    private static void ValidateInputs(ReportNarrativeDto narrative, ReportContextDto context)
    {
        if (string.IsNullOrWhiteSpace(narrative.ExecutiveSummary))
            throw new ReportRenderException("ReportNarrativeDto.ExecutiveSummary is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(narrative.RevenueNarrative))
            throw new ReportRenderException("ReportNarrativeDto.RevenueNarrative is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(narrative.GuestFeedbackNarrative))
            throw new ReportRenderException("ReportNarrativeDto.GuestFeedbackNarrative is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(narrative.OperationalNarrative))
            throw new ReportRenderException("ReportNarrativeDto.OperationalNarrative is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(narrative.IssuesAndResolutions))
            throw new ReportRenderException("ReportNarrativeDto.IssuesAndResolutions is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(narrative.Recommendations))
            throw new ReportRenderException("ReportNarrativeDto.Recommendations is required and cannot be empty.");
        if (string.IsNullOrWhiteSpace(context.Property.Name))
            throw new ReportRenderException("ReportContextDto.Property.Name is required and cannot be empty.");
    }

    private static byte[] RenderPdf(ReportNarrativeDto narrative, ReportContextDto context, Guid reportId)
    {
        byte[] revenueBarChart = context.Revenue.ByMonth.Count > 0
            ? RevenueChartRenderer.RenderMonthlyRevenueBars(context.Revenue.ByMonth)
            : [];

        byte[] occupancyAdrChart = context.Revenue.ByMonth.Count > 0
            ? RevenueChartRenderer.RenderOccupancyAdrOverlay(
                context.Revenue.ByMonth, context.Revenue.OccupancyRate, context.Revenue.Adr)
            : [];

        byte[] ratingChart = context.Reviews.Distribution.Count > 0
            ? RatingDistributionChartRenderer.RenderStarDistribution(context.Reviews.Distribution)
            : [];

        string preparedOn = DateTimeOffset.UtcNow.ToString("MMMM d, yyyy");
        string period = FormatPeriodDisplay(context.Revenue);
        IReadOnlyList<ReviewSnippetDto> topReviews = SelectRepresentativeReviews(context.Reviews.RecentReviews);

        IDocument document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(style => style.FontSize(10).FontFamily(Fonts.Arial));

                page.Header().Element(ComposeHeader);

                page.Content().Column(col =>
                {
                    ComposeCoverPage(col, context, period, preparedOn);
                    col.Item().PageBreak();

                    ComposeSection(col, "Executive Summary", narrative.ExecutiveSummary);

                    ComposeRevenueSection(col, narrative, context, revenueBarChart, occupancyAdrChart);

                    ComposeGuestFeedbackSection(col, narrative, context, ratingChart, topReviews);

                    ComposeOperationalSection(col, narrative, context);

                    ComposeSection(col, "Issues & Resolutions", narrative.IssuesAndResolutions);

                    ComposeSection(col, "Recommendations", narrative.Recommendations);
                });

                page.Footer().Element(footer => ComposeFooter(footer, preparedOn));
            });
        }).WithSettings(new DocumentSettings { PdfA = true })
          .WithMetadata(new DocumentMetadata
          {
              Title = $"Owner Report — {context.Property.Name} {period}",
              Author = "StrataReport AI",
              Creator = "StrataReport AI",
          });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container)
    {
        container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingBottom(5)
            .Text("StrataReport AI").FontSize(8).FontColor(Colors.Grey.Medium);
    }

    private static void ComposeCoverPage(ColumnDescriptor col, ReportContextDto context, string period, string preparedOn)
    {
        col.Item().PaddingTop(80).AlignCenter().Column(cover =>
        {
            cover.Item().AlignCenter().Text(context.Property.Name)
                .FontSize(28).Bold().FontColor(Colors.Black);

            cover.Item().PaddingTop(12).AlignCenter().Text(period)
                .FontSize(18).FontColor(Colors.Grey.Darken2);

            if (!string.IsNullOrWhiteSpace(context.Property.OwnerName))
            {
                cover.Item().PaddingTop(24).AlignCenter()
                    .Text($"Prepared for: {context.Property.OwnerName}")
                    .FontSize(12).FontColor(Colors.Grey.Darken1);
            }

            cover.Item().PaddingTop(8).AlignCenter()
                .Text($"Prepared on: {preparedOn}")
                .FontSize(11).FontColor(Colors.Grey.Darken1);

            if (!string.IsNullOrWhiteSpace(context.Property.AddressLine1))
            {
                string address = BuildAddress(context.Property);
                cover.Item().PaddingTop(24).AlignCenter()
                    .Text(address).FontSize(10).FontColor(Colors.Grey.Medium);
            }
        });
    }

    private static string BuildAddress(PropertyMetadataDto prop)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(prop.AddressLine1)) parts.Add(prop.AddressLine1);
        if (!string.IsNullOrWhiteSpace(prop.City)) parts.Add(prop.City);
        if (!string.IsNullOrWhiteSpace(prop.State)) parts.Add(prop.State);
        if (!string.IsNullOrWhiteSpace(prop.PostalCode)) parts.Add(prop.PostalCode);
        return string.Join(", ", parts);
    }

    private static void ComposeRevenueSection(
        ColumnDescriptor col,
        ReportNarrativeDto narrative,
        ReportContextDto context,
        byte[] revenueBarChart,
        byte[] occupancyAdrChart)
    {
        col.Item().PaddingTop(16).Element(e => ComposeSectionHeader(e, "Revenue Performance"));

        col.Item().PaddingTop(8).Text(narrative.RevenueNarrative).FontSize(10).LineHeight(1.4f);

        if (revenueBarChart.Length > 0)
        {
            col.Item().PaddingTop(12).AlignCenter()
                .Image(revenueBarChart).FitWidth();
        }

        if (occupancyAdrChart.Length > 0)
        {
            col.Item().PaddingTop(12).AlignCenter()
                .Image(occupancyAdrChart).FitWidth();
        }
    }

    private static void ComposeGuestFeedbackSection(
        ColumnDescriptor col,
        ReportNarrativeDto narrative,
        ReportContextDto context,
        byte[] ratingChart,
        IReadOnlyList<ReviewSnippetDto> topReviews)
    {
        col.Item().PaddingTop(16).Element(e => ComposeSectionHeader(e, "Guest Feedback"));

        col.Item().PaddingTop(8).Text(narrative.GuestFeedbackNarrative).FontSize(10).LineHeight(1.4f);

        if (ratingChart.Length > 0)
        {
            col.Item().PaddingTop(12).AlignCenter()
                .Image(ratingChart).FitWidth();
        }

        if (topReviews.Count > 0)
        {
            col.Item().PaddingTop(12).Column(reviews =>
            {
                reviews.Item().Text("Representative Reviews").FontSize(11).Bold();

                foreach (ReviewSnippetDto review in topReviews)
                {
                    reviews.Item().PaddingTop(6).BorderLeft(3).BorderColor(Colors.Blue.Lighten3)
                        .PaddingLeft(8).Column(r =>
                        {
                            r.Item().Text(t =>
                            {
                                t.Span($"{review.Rating:F1}★ ").Bold();
                                t.Span($"— {review.Platform}, {review.ReviewDate:MMM yyyy}")
                                    .FontColor(Colors.Grey.Darken1);
                            });
                            if (!string.IsNullOrWhiteSpace(review.ReviewText))
                            {
                                r.Item().PaddingTop(2)
                                    .Text($"\"{review.ReviewText}\"")
                                    .Italic().FontColor(Colors.Grey.Darken2);
                            }
                        });
                }
            });
        }
    }

    private static void ComposeOperationalSection(
        ColumnDescriptor col,
        ReportNarrativeDto narrative,
        ReportContextDto context)
    {
        col.Item().PaddingTop(16).Element(e => ComposeSectionHeader(e, "Operational Activity"));

        col.Item().PaddingTop(8).Text(narrative.OperationalNarrative).FontSize(10).LineHeight(1.4f);

        col.Item().PaddingTop(12).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(3);
                cols.RelativeColumn(1);
                cols.RelativeColumn(1);
                cols.RelativeColumn(2);
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Grey.Lighten3).Padding(4)
                    .Text("Task Type").Bold().FontSize(9);
                header.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignCenter()
                    .Text("Total").Bold().FontSize(9);
                header.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignCenter()
                    .Text("Completed").Bold().FontSize(9);
                header.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignCenter()
                    .Text("On-Time Rate").Bold().FontSize(9);
            });

            table.Cell().Padding(4).Text("All Tasks").FontSize(9);
            table.Cell().Padding(4).AlignCenter()
                .Text(context.Tasks.Total.ToString()).FontSize(9);
            table.Cell().Padding(4).AlignCenter()
                .Text(((int)(context.Tasks.Total * context.Tasks.CompletionRate / 100m)).ToString()).FontSize(9);
            table.Cell().Padding(4).AlignCenter()
                .Text($"{context.Tasks.OnTimeRate:F1}%").FontSize(9);
        });

        if (context.Inspections.Count > 0 && context.Inspections.AverageScore.HasValue)
        {
            col.Item().PaddingTop(8)
                .Text($"Inspection Score: {context.Inspections.AverageScore:F1} " +
                      $"(Issues found: {context.Inspections.TotalIssuesFound}, " +
                      $"resolved: {context.Inspections.TotalIssuesResolved})")
                .FontSize(10);
        }
    }

    private static void ComposeSection(ColumnDescriptor col, string title, string body)
    {
        col.Item().PaddingTop(16).Element(e => ComposeSectionHeader(e, title));
        col.Item().PaddingTop(8).Text(body).FontSize(10).LineHeight(1.4f);
    }

    private static void ComposeSectionHeader(IContainer container, string title)
    {
        container.BorderBottom(2).BorderColor(Colors.Blue.Darken1).PaddingBottom(4)
            .Text(title).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
    }

    private static void ComposeFooter(IContainer container, string preparedOn)
    {
        container.BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4)
            .Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.Span("Generated by StrataReport AI on ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(preparedOn).FontSize(8).FontColor(Colors.Grey.Medium);
                });
                row.ConstantItem(60).AlignRight().Text(t =>
                {
                    t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" of ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
    }

    private static IReadOnlyList<ReviewSnippetDto> SelectRepresentativeReviews(
        IReadOnlyList<ReviewSnippetDto> reviews)
    {
        if (reviews.Count == 0) return [];

        List<ReviewSnippetDto> withText = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.ReviewText))
            .ToList();

        if (withText.Count == 0) return [];

        List<ReviewSnippetDto> selected = [];
        ReviewSnippetDto? highest = withText.MaxBy(r => r.Rating);
        if (highest is not null) selected.Add(highest);

        ReviewSnippetDto? lowest = withText
            .Where(r => r != highest)
            .MinBy(r => r.Rating);
        if (lowest is not null) selected.Add(lowest);

        if (selected.Count < 3)
        {
            ReviewSnippetDto? middle = withText
                .Where(r => r != highest && r != lowest)
                .OrderByDescending(r => r.ReviewDate)
                .FirstOrDefault();
            if (middle is not null) selected.Add(middle);
        }

        return selected;
    }

    private static string FormatPeriodDisplay(RevenueSummaryDto revenue)
    {
        if (revenue.ByMonth.Count == 0) return "Report Period";

        int minYear = revenue.ByMonth.Min(m => m.Year);
        int maxYear = revenue.ByMonth.Max(m => m.Year);
        int minMonth = revenue.ByMonth.Where(m => m.Year == minYear).Min(m => m.Month);
        int maxMonth = revenue.ByMonth.Where(m => m.Year == maxYear).Max(m => m.Month);

        if (minYear == maxYear && (maxMonth - minMonth) <= 2)
        {
            int quarter = (minMonth - 1) / 3 + 1;
            return $"Q{quarter} {minYear}";
        }

        string startLabel = new DateOnly(minYear, minMonth, 1).ToString("MMM yyyy");
        string endLabel = new DateOnly(maxYear, maxMonth, 1).ToString("MMM yyyy");
        return $"{startLabel} – {endLabel}";
    }

    private static string FormatPeriodForFilename(RevenueSummaryDto revenue)
    {
        if (revenue.ByMonth.Count == 0) return "Report";

        int minYear = revenue.ByMonth.Min(m => m.Year);
        int maxYear = revenue.ByMonth.Max(m => m.Year);
        int minMonth = revenue.ByMonth.Where(m => m.Year == minYear).Min(m => m.Month);
        int maxMonth = revenue.ByMonth.Where(m => m.Year == maxYear).Max(m => m.Month);

        if (minYear == maxYear && (maxMonth - minMonth) <= 2)
        {
            int quarter = (minMonth - 1) / 3 + 1;
            return $"Q{quarter}-{minYear}";
        }

        return $"{minYear}-{minMonth:D2}_to_{maxYear}-{maxMonth:D2}";
    }
}
