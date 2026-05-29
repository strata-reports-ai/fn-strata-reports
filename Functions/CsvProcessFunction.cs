using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class CsvProcessFunction(
    ILogger<CsvProcessFunction> logger,
    AppDbContext db,
    IConfiguration configuration)
{
    private static readonly IReadOnlyDictionary<string, string[]> RevenueColumnSynonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["booking_external_id"] = ["Reservation ID", "Booking ID", "Confirmation Code", "Confirmation Number"],
            ["checkin_date"] = ["Check-in", "Check In", "Arrival Date", "Checkin Date"],
            ["checkout_date"] = ["Check-out", "Check Out", "Departure Date", "Checkout Date"],
            ["gross_revenue"] = ["Gross Revenue", "Gross Earnings", "Payout", "Total Payout"],
            ["platform"] = ["Platform", "Channel", "Source"],
        };

    private static readonly IReadOnlyDictionary<string, string[]> ExpenseColumnSynonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["expense_date"] = ["Date", "Transaction Date", "Expense Date"],
            ["category"] = ["Category", "Type", "Expense Type"],
            ["amount"] = ["Amount", "Total", "Cost"],
            ["vendor"] = ["Vendor", "Payee", "Supplier"],
        };

    private static readonly IReadOnlyDictionary<string, string[]> ReviewColumnSynonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["review_date"] = ["Review Date", "Date", "Posted Date"],
            ["rating"] = ["Rating", "Score", "Stars"],
            ["review_text"] = ["Review", "Comment", "Review Text"],
            ["platform"] = ["Platform", "Channel", "Source"],
        };

    [Function("CsvProcessFunction")]
    public async Task Run(
        [QueueTrigger("csv-process", Connection = "AzureWebJobsStorage")] string messageJson,
        CancellationToken ct)
    {
        long startMs = Stopwatch.GetTimestamp();

        CsvProcessMessage? message = JsonSerializer.Deserialize<CsvProcessMessage>(messageJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (message is null)
        {
            logger.LogError("Failed to deserialize csv-process queue message: {Raw}", messageJson);
            return;
        }

        logger.LogInformation(
            "Processing CSV import {ImportId} type={ImportType} tenant={TenantId} property={PropertyId}",
            message.ImportId, message.ImportType, message.TenantId, message.PropertyId);

        await db.Database.ExecuteSqlRawAsync(
            "SET app.current_tenant_id = {0}", message.TenantId.ToString(), ct);

        Import? import = await db.Imports.FindAsync([message.ImportId], ct);
        if (import is null)
        {
            logger.LogError("Import record {ImportId} not found", message.ImportId);
            return;
        }

        string connectionString = configuration["AzureWebJobsStorage"]
            ?? configuration["ConnectionStrings:Storage"]
            ?? string.Empty;

        byte[] fileBytes;
        try
        {
            fileBytes = await DownloadBlobAsync(connectionString, message.BlobPath, ct);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or InvalidOperationException)
        {
            logger.LogError(ex, "Failed to download blob {BlobPath}", message.BlobPath);
            await FailImport(import, $"Could not download file: {ex.Message}", ct);
            return;
        }

        string checksum = ComputeSha256Hex(fileBytes);

        bool duplicate = await db.Imports
            .AnyAsync(i =>
                i.Id != message.ImportId &&
                i.TenantId == message.TenantId &&
                i.PropertyId == message.PropertyId &&
                i.ChecksumSha256 == checksum &&
                i.Status != "failed",
                ct);

        if (duplicate)
        {
            await FailImport(import,
                "Duplicate file — this file has already been imported",
                ct,
                checksum);
            return;
        }

        import.ChecksumSha256 = checksum;

        ProcessResult result = message.ImportType.ToLowerInvariant() switch
        {
            "revenue" => await ProcessRevenueAsync(fileBytes, import, message, ct),
            "expense" or "expenses" => await ProcessExpensesAsync(fileBytes, import, message, ct),
            "review" or "reviews" => await ProcessReviewsAsync(fileBytes, import, message, ct),
            _ => ProcessUnsupportedType(message.ImportType),
        };

        import.Status = result.Status;
        import.RecordsTotal = result.RecordsTotal;
        import.RecordsImported = result.RecordsImported;
        import.RecordsSkipped = result.RecordsSkipped;
        import.ErrorSummary = result.ErrorSummary;
        import.ColumnMapping = result.ColumnMapping;
        import.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        long elapsedMs = (long)Stopwatch.GetElapsedTime(startMs).TotalMilliseconds;
        logger.LogInformation(
            "import.processing_ms={ElapsedMs} importType={ImportType} importId={ImportId} status={Status}",
            elapsedMs, message.ImportType, message.ImportId, result.Status);
    }

    private async Task<ProcessResult> ProcessRevenueAsync(
        byte[] fileBytes, Import import, CsvProcessMessage message, CancellationToken ct)
    {
        using MemoryStream stream = new(fileBytes);
        using StreamReader reader = new(stream, Encoding.UTF8);

        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };

        using CsvReader csv = new(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        string[]? headers = csv.HeaderRecord;
        if (headers is null)
            return FailedResult("CSV file has no header row.");

        Dictionary<string, string>? resolved = ResolveColumns(headers, RevenueColumnSynonyms,
            ["booking_external_id", "checkin_date", "checkout_date", "gross_revenue"],
            out string? missingError);

        if (resolved is null)
            return FailedResult(missingError!);

        string columnMappingJson = JsonSerializer.Serialize(resolved);

        int total = 0;
        int imported = 0;
        int skipped = 0;
        List<string> rowErrors = new();

        while (await csv.ReadAsync())
        {
            total++;
            string rowContext = $"row {total + 1}";

            try
            {
                string bookingId = GetField(csv, resolved["booking_external_id"]);
                string checkinRaw = GetField(csv, resolved["checkin_date"]);
                string checkoutRaw = GetField(csv, resolved["checkout_date"]);
                string grossRaw = GetField(csv, resolved["gross_revenue"]);
                string platform = resolved.TryGetValue("platform", out string? platformCol)
                    ? GetField(csv, platformCol)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(bookingId))
                {
                    rowErrors.Add($"{rowContext}: booking_external_id is empty");
                    skipped++;
                    continue;
                }

                if (!TryParseDate(checkinRaw, out DateOnly checkinDate))
                {
                    rowErrors.Add($"{rowContext}: invalid checkin_date '{checkinRaw}'");
                    skipped++;
                    continue;
                }

                if (!TryParseDate(checkoutRaw, out DateOnly checkoutDate))
                {
                    rowErrors.Add($"{rowContext}: invalid checkout_date '{checkoutRaw}'");
                    skipped++;
                    continue;
                }

                if (!TryParseDecimal(grossRaw, out decimal grossRevenue))
                {
                    rowErrors.Add($"{rowContext}: invalid gross_revenue '{grossRaw}'");
                    skipped++;
                    continue;
                }

                string? guestNameRaw = TryGetOptionalField(csv, "Guest Name", "Guest", "Name");
                string? guestNameHash = guestNameRaw is not null
                    ? HashGuestName(guestNameRaw)
                    : null;

                int nights = checkoutDate.DayNumber - checkinDate.DayNumber;
                if (nights < 0) nights = 0;

                Guid propertyId = message.PropertyId ?? Guid.Empty;

                RevenueRecord? existing = await db.RevenueRecords
                    .FirstOrDefaultAsync(r =>
                        r.TenantId == message.TenantId &&
                        r.PropertyId == propertyId &&
                        r.BookingExternalId == bookingId,
                        ct);

                if (existing is not null)
                {
                    existing.ImportId = import.Id;
                    existing.Platform = string.IsNullOrWhiteSpace(platform) ? existing.Platform : platform;
                    existing.CheckinDate = checkinDate;
                    existing.CheckoutDate = checkoutDate;
                    existing.Nights = nights;
                    existing.GrossRevenue = grossRevenue;
                    existing.GuestNameHash = guestNameHash ?? existing.GuestNameHash;
                }
                else
                {
                    RevenueRecord record = new()
                    {
                        Id = Guid.NewGuid(),
                        TenantId = message.TenantId,
                        PropertyId = propertyId,
                        ImportId = import.Id,
                        BookingExternalId = bookingId,
                        Platform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform,
                        CheckinDate = checkinDate,
                        CheckoutDate = checkoutDate,
                        Nights = nights,
                        GrossRevenue = grossRevenue,
                        GuestNameHash = guestNameHash,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                    db.RevenueRecords.Add(record);
                }

                imported++;
            }
            catch (CsvHelperException ex)
            {
                rowErrors.Add($"{rowContext}: parse error — {ex.Message}");
                skipped++;
            }
        }

        string status = DetermineStatus(imported, skipped);
        string? errorSummary = rowErrors.Count > 0
            ? string.Join("; ", rowErrors.Take(10)) + (rowErrors.Count > 10 ? $" (+{rowErrors.Count - 10} more)" : "")
            : null;

        return new ProcessResult(status, total, imported, skipped, errorSummary, columnMappingJson);
    }

    private async Task<ProcessResult> ProcessExpensesAsync(
        byte[] fileBytes, Import import, CsvProcessMessage message, CancellationToken ct)
    {
        using MemoryStream stream = new(fileBytes);
        using StreamReader reader = new(stream, Encoding.UTF8);

        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };

        using CsvReader csv = new(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        string[]? headers = csv.HeaderRecord;
        if (headers is null)
            return FailedResult("CSV file has no header row.");

        Dictionary<string, string>? resolved = ResolveColumns(headers, ExpenseColumnSynonyms,
            ["expense_date", "category", "amount"],
            out string? missingError);

        if (resolved is null)
            return FailedResult(missingError!);

        string columnMappingJson = JsonSerializer.Serialize(resolved);

        int total = 0;
        int imported = 0;
        int skipped = 0;
        List<string> rowErrors = new();

        while (await csv.ReadAsync())
        {
            total++;
            string rowContext = $"row {total + 1}";

            try
            {
                string dateRaw = GetField(csv, resolved["expense_date"]);
                string category = GetField(csv, resolved["category"]);
                string amountRaw = GetField(csv, resolved["amount"]);
                string? vendor = resolved.TryGetValue("vendor", out string? vendorCol)
                    ? GetField(csv, vendorCol)
                    : null;

                if (!TryParseDate(dateRaw, out DateOnly expenseDate))
                {
                    rowErrors.Add($"{rowContext}: invalid expense_date '{dateRaw}'");
                    skipped++;
                    continue;
                }

                if (!TryParseDecimal(amountRaw, out decimal amount))
                {
                    rowErrors.Add($"{rowContext}: invalid amount '{amountRaw}'");
                    skipped++;
                    continue;
                }

                Guid propertyId = message.PropertyId ?? Guid.Empty;

                ExpenseRecord record = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = message.TenantId,
                    PropertyId = propertyId,
                    ImportId = import.Id,
                    ExpenseDate = expenseDate,
                    Category = string.IsNullOrWhiteSpace(category) ? "Uncategorized" : category,
                    Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor,
                    Amount = amount,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.ExpenseRecords.Add(record);
                imported++;
            }
            catch (CsvHelperException ex)
            {
                rowErrors.Add($"{rowContext}: parse error — {ex.Message}");
                skipped++;
            }
        }

        string status = DetermineStatus(imported, skipped);
        string? errorSummary = rowErrors.Count > 0
            ? string.Join("; ", rowErrors.Take(10)) + (rowErrors.Count > 10 ? $" (+{rowErrors.Count - 10} more)" : "")
            : null;

        return new ProcessResult(status, total, imported, skipped, errorSummary, columnMappingJson);
    }

    private async Task<ProcessResult> ProcessReviewsAsync(
        byte[] fileBytes, Import import, CsvProcessMessage message, CancellationToken ct)
    {
        using MemoryStream stream = new(fileBytes);
        using StreamReader reader = new(stream, Encoding.UTF8);

        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };

        using CsvReader csv = new(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        string[]? headers = csv.HeaderRecord;
        if (headers is null)
            return FailedResult("CSV file has no header row.");

        Dictionary<string, string>? resolved = ResolveColumns(headers, ReviewColumnSynonyms,
            ["review_date", "rating", "platform"],
            out string? missingError);

        if (resolved is null)
            return FailedResult(missingError!);

        string columnMappingJson = JsonSerializer.Serialize(resolved);

        int total = 0;
        int imported = 0;
        int skipped = 0;
        List<string> rowErrors = new();

        while (await csv.ReadAsync())
        {
            total++;
            string rowContext = $"row {total + 1}";

            try
            {
                string dateRaw = GetField(csv, resolved["review_date"]);
                string ratingRaw = GetField(csv, resolved["rating"]);
                string platform = GetField(csv, resolved["platform"]);
                string? reviewText = resolved.TryGetValue("review_text", out string? reviewCol)
                    ? GetField(csv, reviewCol)
                    : null;

                if (!TryParseDate(dateRaw, out DateOnly reviewDate))
                {
                    rowErrors.Add($"{rowContext}: invalid review_date '{dateRaw}'");
                    skipped++;
                    continue;
                }

                if (!TryParseDecimal(ratingRaw, out decimal rating))
                {
                    rowErrors.Add($"{rowContext}: invalid rating '{ratingRaw}'");
                    skipped++;
                    continue;
                }

                string? guestNameRaw = TryGetOptionalField(csv, "Guest Name", "Guest", "Reviewer");
                string? guestNameHash = guestNameRaw is not null
                    ? HashGuestName(guestNameRaw)
                    : null;

                Guid propertyId = message.PropertyId ?? Guid.Empty;

                ReviewRecord record = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = message.TenantId,
                    PropertyId = propertyId,
                    ImportId = import.Id,
                    Platform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform,
                    ReviewDate = reviewDate,
                    Rating = rating,
                    ReviewText = string.IsNullOrWhiteSpace(reviewText) ? null : reviewText,
                    GuestNameHash = guestNameHash,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.ReviewRecords.Add(record);
                imported++;
            }
            catch (CsvHelperException ex)
            {
                rowErrors.Add($"{rowContext}: parse error — {ex.Message}");
                skipped++;
            }
        }

        string status = DetermineStatus(imported, skipped);
        string? errorSummary = rowErrors.Count > 0
            ? string.Join("; ", rowErrors.Take(10)) + (rowErrors.Count > 10 ? $" (+{rowErrors.Count - 10} more)" : "")
            : null;

        return new ProcessResult(status, total, imported, skipped, errorSummary, columnMappingJson);
    }

    private static ProcessResult ProcessUnsupportedType(string importType)
    {
        return new ProcessResult(
            Status: "partial",
            RecordsTotal: 0,
            RecordsImported: 0,
            RecordsSkipped: 0,
            ErrorSummary: $"{importType} import coming soon",
            ColumnMapping: null);
    }

    private static Dictionary<string, string>? ResolveColumns(
        string[] headers,
        IReadOnlyDictionary<string, string[]> synonymMap,
        string[] requiredFields,
        out string? missingError)
    {
        Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string[]> kv in synonymMap)
        {
            foreach (string synonym in kv.Value)
            {
                string? match = headers.FirstOrDefault(h =>
                    string.Equals(h.Trim(), synonym, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    resolved[kv.Key] = match;
                    break;
                }
            }
        }

        foreach (string required in requiredFields)
        {
            if (!resolved.ContainsKey(required))
            {
                string[] synonyms = synonymMap.TryGetValue(required, out string[]? s) ? s : [];
                missingError = $"Could not find a column for '{required}'. Expected one of: {string.Join(", ", synonyms)}";
                return null;
            }
        }

        missingError = null;
        return resolved;
    }

    private static string GetField(CsvReader csv, string headerName)
    {
        return csv.GetField(headerName) ?? string.Empty;
    }

    private static string? TryGetOptionalField(CsvReader csv, params string[] candidates)
    {
        string[]? headers = csv.HeaderRecord;
        if (headers is null) return null;

        foreach (string candidate in candidates)
        {
            string? match = headers.FirstOrDefault(h =>
                string.Equals(h.Trim(), candidate, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                string? value = csv.GetField(match);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static bool TryParseDate(string raw, out DateOnly date)
    {
        string[] formats =
        [
            "yyyy-MM-dd",
            "MM/dd/yyyy",
            "M/d/yyyy",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "yyyy/MM/dd",
            "MMM d, yyyy",
            "MMMM d, yyyy",
            "d MMM yyyy",
        ];

        if (DateOnly.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date))
            return true;

        if (DateOnly.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date))
            return true;

        return false;
    }

    private static bool TryParseDecimal(string raw, out decimal value)
    {
        string cleaned = raw.Trim().TrimStart('$').Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string DetermineStatus(int imported, int skipped)
    {
        if (imported == 0) return "failed";
        if (skipped > 0) return "partial";
        return "succeeded";
    }

    private static ProcessResult FailedResult(string error)
        => new("failed", 0, 0, 0, error, null);

    private async Task FailImport(Import import, string error, CancellationToken ct, string? checksum = null)
    {
        import.Status = "failed";
        import.ErrorSummary = error;
        import.UpdatedAt = DateTimeOffset.UtcNow;
        if (checksum is not null)
            import.ChecksumSha256 = checksum;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Import {ImportId} failed: {Error}", import.Id, error);
    }

    private static async Task<byte[]> DownloadBlobAsync(
        string connectionString, string blobPath, CancellationToken ct)
    {
        int slashIndex = blobPath.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
            throw new InvalidOperationException($"Invalid blob path (no container separator): {blobPath}");

        string containerName = blobPath[..slashIndex];
        string blobName = blobPath[(slashIndex + 1)..];

        BlobContainerClient container = new BlobServiceClient(connectionString)
            .GetBlobContainerClient(containerName);
        BlobClient blob = container.GetBlobClient(blobName);

        using MemoryStream ms = new();
        await blob.DownloadToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string ComputeSha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string HashGuestName(string guestName)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(guestName.Trim().ToLowerInvariant()))).ToLowerInvariant();

    private sealed record ProcessResult(
        string Status,
        int RecordsTotal,
        int RecordsImported,
        int RecordsSkipped,
        string? ErrorSummary,
        string? ColumnMapping);

    private sealed record CsvProcessMessage(
        Guid ImportId,
        Guid TenantId,
        Guid? PropertyId,
        string ImportType,
        string BlobPath);
}
