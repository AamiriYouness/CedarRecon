using CedarRecon.Domain;
using CedarRecon.Domain.Common;
using CedarRecon.Domain.Entities;
using CedarRecon.Domain.Errors;
using CedarRecon.Domain.Pipelines;
using CedarRecon.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Buffers;
namespace CedarRecon.Application.Normalization;

public class NormalizationEngine(
    ILogger<NormalizationEngine> logger,
    ReconciliationOptions? defaultOptions = null) : INormalizationEngine
{
    private readonly ILogger<NormalizationEngine> _logger = logger;
    private readonly ReconciliationOptions _defaultOptions = defaultOptions ?? new ReconciliationOptions();

    // Precomputed char set for reference stripping (avoids repeated allocations)
    private static readonly SearchValues<char> ReferenceStripChars =
        SearchValues.Create([' ', '-', '/']);

    public Result<Transaction> Normalize(RawTransaction raw)
    {
        try
        {
            // Validate required fields before allocating
            if (string.IsNullOrWhiteSpace(raw.Reference))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    "Reference is required.", raw.RowNumber, "Reference"));

            if (string.IsNullOrWhiteSpace(raw.Amount))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    "Amount is required.", raw.RowNumber, "Amount"));

            if (string.IsNullOrWhiteSpace(raw.Currency))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    "Currency is required.", raw.RowNumber, "Currency"));

            if (string.IsNullOrWhiteSpace(raw.ValueDate))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    "ValueDate is required.", raw.RowNumber, "ValueDate"));

            // Parse amount — catch format errors explicitly
            if (!TryParseAmount(raw.Amount, out var parsedAmount))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    $"Cannot parse amount '{raw.Amount}'.", raw.RowNumber, "Amount"));

            // Parse date — catch format errors explicitly
            if (!TryParseDate(raw.ValueDate, out var parsedDate))
                return Result<Transaction>.Fail(new InvalidTransactionError(
                    $"Cannot parse date '{raw.ValueDate}'.", raw.RowNumber, "ValueDate"));

            var normalizedRef = NormalizeReference(raw.Reference);
            var normalizedCurrency = NormalizeCurrency(raw.Currency);
            var normalizedAmount = NormalizeAmount(parsedAmount);
            var normalizedDescription = NormalizeDescription(raw.Description ?? string.Empty);
            var normalizedIban = raw.Iban is not null ? NormalizeIban(raw.Iban) : null;

            DateTimeOffset? bookingDate = null;
            if (!string.IsNullOrWhiteSpace(raw.BookingDate))
            {
                if (TryParseDate(raw.BookingDate, out var bd))
                    bookingDate = bd;
                // Non-fatal: missing booking date doesn't invalidate transaction
            }

            var transaction = new Transaction
            {
                Id = TransactionId.New(),
                NormalizedReference = TransactionReference.FromNormalized(normalizedRef),
                Amount = Money.Of(normalizedAmount, normalizedCurrency),
                ValueDate = parsedDate,
                Description = normalizedDescription,
                Iban = normalizedIban,
                CounterpartyName = raw.CounterpartyName?.Trim(),
                BookingDate = bookingDate,
                SourceFileName = raw.SourceFileName,
                SourceRowNumber = raw.RowNumber
            };

            return Result<Transaction>.Ok(transaction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Normalization failed for row {RowNumber} in {FileName}",
                raw.RowNumber, raw.SourceFileName);

            return Result<Transaction>.Fail(new InvalidTransactionError(
                $"Normalization failed: {ex.Message}", raw.RowNumber));
        }
    }

    /// <summary>
    /// Strips spaces, dashes, slashes. Uppercases. Zero allocation via Span.
    /// Idempotent: running twice produces identical output.
    /// </summary>
    public string NormalizeReference(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var span = raw.AsSpan().Trim();

        // Fast path: if no strippable chars and already uppercase, avoid allocation
        if (!span.ContainsAny(ReferenceStripChars) && IsAllUpperAscii(span))
            return span == raw.AsSpan() ? raw : span.ToString();

        // Allocate output buffer — at most as long as input
        var buffer = span.Length <= 256
            ? stackalloc char[span.Length]
            : new char[span.Length];

        var writeIndex = 0;
        foreach (var ch in span)
        {
            if (ch is ' ' or '-' or '/')
                continue;
            buffer[writeIndex++] = char.ToUpperInvariant(ch);
        }

        return buffer[..writeIndex].ToString();
    }

    /// <summary>
    /// Normalizes amount string to decimal, then rounds to 2dp AwayFromZero.
    /// Idempotent at the decimal level.
    /// </summary>
    public decimal NormalizeAmount(string raw)
    {
        if (!TryParseAmount(raw, out var parsed))
            throw new FormatException($"Cannot parse amount: '{raw}'");

        return NormalizeAmount(parsed);
    }

    private static decimal NormalizeAmount(decimal parsed) =>
        Math.Round(parsed, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Normalizes date string to UTC DateTimeOffset.
    /// Idempotent: calling twice produces same UTC value.
    /// </summary>
    public DateTimeOffset NormalizeDate(string raw)
    {
        if (!TryParseDate(raw, out var result))
            throw new FormatException($"Cannot parse date: '{raw}'");

        return result;
    }

    /// <summary>
    /// Trims and uppercases description. Idempotent.
    /// </summary>
    public string NormalizeDescription(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var span = raw.AsSpan().Trim();
        if (span.IsEmpty)
            return string.Empty;

        // Fast path: already uppercase, no leading/trailing whitespace
        if (IsAllUpperAscii(span) && span.Length == raw.Length)
            return raw;

        return span.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Strips spaces from IBAN. Idempotent.
    /// Input: "GB29 NWBK 6016 1331 9268 19"
    /// Output: "GB29NWBK60161331926819"
    /// </summary>
    public string NormalizeIban(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var span = raw.AsSpan().Trim();

        // Count non-space chars to size buffer
        var nonSpaceCount = 0;
        foreach (var ch in span)
            if (ch != ' ') nonSpaceCount++;

        if (nonSpaceCount == span.Length)
            return span == raw.AsSpan() ? raw : span.ToString();

        var buffer = nonSpaceCount <= 64
            ? stackalloc char[nonSpaceCount]
            : new char[nonSpaceCount];

        var wi = 0;
        foreach (var ch in span)
            if (ch != ' ')
                buffer[wi++] = char.ToUpperInvariant(ch);

        return buffer.ToString();
    }

    /// <summary>
    /// Normalizes currency to ISO 4217 uppercase. Idempotent.
    /// </summary>
    public string NormalizeCurrency(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Currency cannot be empty.", nameof(raw));

        var trimmed = raw.AsSpan().Trim();
        if (trimmed.IsEmpty)
            throw new ArgumentException("Currency cannot be whitespace only.", nameof(raw));

        // Fast path: already uppercase ASCII, length 3
        if (trimmed.Length == 3 && IsAllUpperAscii(trimmed))
            return trimmed == raw.AsSpan() ? raw : trimmed.ToString();

        var result = trimmed.ToString().ToUpperInvariant();

        if (result.Length != 3)
            throw new ArgumentException($"Currency '{raw}' does not produce a 3-char ISO 4217 code.", nameof(raw));

        return result;
    }

    private static bool TryParseAmount(string raw, out decimal result)
    {
        var span = raw.AsSpan().Trim();

        int dotIndex = span.LastIndexOf('.');
        int commaIndex = span.LastIndexOf(',');

        Span<char> normalized = stackalloc char[span.Length];

        if (commaIndex > dotIndex)
        {
            // Comma is the decimal separator: "1.234,56" → "1234.56"
            for (var i = 0; i < span.Length; i++)
            {
                normalized[i] = span[i] switch
                {
                    ',' => '.',
                    '.' => '\0',  // strip thousands dots
                    _ => span[i]
                };
            }
            // Compact out the '\0' chars
            var written = 0;
            Span<char> compacted = stackalloc char[span.Length];
            for (var i = 0; i < span.Length; i++)
                if (normalized[i] != '\0') compacted[written++] = normalized[i];

            return decimal.TryParse(
                compacted[..written],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);
        }
        else
        {
            // Dot is the decimal separator (or no comma): "1,234.56" → strip commas
            for (var i = 0; i < span.Length; i++)
                normalized[i] = span[i] == ',' ? '\0' : span[i];

            var written = 0;
            Span<char> compacted = stackalloc char[span.Length];
            for (var i = 0; i < span.Length; i++)
                if (normalized[i] != '\0') compacted[written++] = normalized[i];

            return decimal.TryParse(
                compacted[..written],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);
        }
    }

    private static bool TryParseDate(string raw, out DateTimeOffset result)
    {
        var span = raw.AsSpan().Trim();

        // Try ISO 8601 first
        if (DateTimeOffset.TryParse(span, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out result))
        {
            result = result.ToUniversalTime();
            return true;
        }

        // Try common banking formats
        Span<string> formats =
        [
            "yyyyMMdd",      // MT940 SWIFT format
            "dd/MM/yyyy",    // European
            "MM/dd/yyyy",    // US
            "yyyy-MM-dd",    // ISO date only
            "dd-MM-yyyy",    // European dashed
            "dd.MM.yyyy"     // German/French
        ];

        foreach (var format in formats)
        {
            if (DateTimeOffset.TryParseExact(
                span,
                format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out result))
            {
                result = result.ToUniversalTime();
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool IsAllUpperAscii(ReadOnlySpan<char> span)
    {
        foreach (var ch in span)
        {
            if (char.IsLower(ch)) return false;
        }
        return true;
    }
}
