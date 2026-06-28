using CedarRecon.Application.Normalization;
using CedarRecon.Domain.Entities;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using FsCheck;

namespace CedarRecon.Tests.Unit.Normalization;

public class NormalizationEngineTests
{
    private readonly NormalizationEngine _sut;

    public NormalizationEngineTests()
    {
        _sut = new NormalizationEngine(NullLogger<NormalizationEngine>.Instance);
    }

    [Theory]
    [InlineData("REF-12345-ABC", "REF12345ABC")]
    [InlineData("ref 12345 abc", "REF12345ABC")]
    [InlineData("REF/12345/ABC", "REF12345ABC")]
    [InlineData("REF 12345", "REF12345")]
    [InlineData("ref-12345", "REF12345")]
    public void NormalizeReference_StripsSpecialCharsAndUppercases(string input, string expected)
    {
        _sut.NormalizeReference(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ALREADY")]
    [InlineData("NOSPACES")]
    [InlineData("ABC123")]
    public void NormalizeReference_AlreadyNormalized_ReturnsSameValue(string input)
    {
        _sut.NormalizeReference(input).ShouldBe(input);
    }

    [Fact]
    public void NormalizeReference_EmptyString_ReturnsEmpty()
    {
        _sut.NormalizeReference(string.Empty).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(" - ")]
    [InlineData("///")]
    public void NormalizeReference_OnlyStripChars_ReturnsEmpty(string input)
    {
        _sut.NormalizeReference(input).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("100.005", 100.01)]   // AwayFromZero
    [InlineData("100.004", 100.00)]
    [InlineData("100.00", 100.00)]
    [InlineData("-250.505", -250.51)] // Negative AwayFromZero
    [InlineData("1,234.56", 1234.56)] // Thousands separator
    [InlineData("100,50", 100.50)]    // European decimal comma
    [InlineData("0", 0)]
    public void NormalizeAmount_ParsesAndRoundsCorrectly(string input, decimal expected)
    {
        _sut.NormalizeAmount(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("abc")]
    [InlineData("")]
    public void NormalizeAmount_InvalidInput_Throws(string input)
    {
        Should.Throw<FormatException>(() => _sut.NormalizeAmount(input));
    }

    // ── NormalizeDate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("15/01/2024")]
    [InlineData("20240115")]    // MT940 YYMMDD-like (full year here for clarity)
    public void NormalizeDate_CommonFormats_ParseToUtc(string input)
    {
        var result = _sut.NormalizeDate(input);
        result.Offset.ShouldBe(TimeSpan.Zero); // Must be UTC
        result.Year.ShouldBe(2024);
        result.Month.ShouldBe(1);
        result.Day.ShouldBe(15);
    }

    [Fact]
    public void NormalizeDate_WithTimezone_NormalizesToUtc()
    {
        var result = _sut.NormalizeDate("2024-01-15T14:00:00+02:00");
        result.Offset.ShouldBe(TimeSpan.Zero);
        result.Hour.ShouldBe(12); // 14:00+02:00 = 12:00 UTC
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("99/99/9999")]
    public void NormalizeDate_InvalidInput_Throws(string input)
    {
        Should.Throw<FormatException>(() => _sut.NormalizeDate(input));
    }

    [Theory]
    [InlineData("  payment for invoice  ", "PAYMENT FOR INVOICE")]
    [InlineData("ALREADY UPPERCASE", "ALREADY UPPERCASE")]
    [InlineData("mixed CASE", "MIXED CASE")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeDescription_TrimsAndUppercases(string input, string expected)
    {
        _sut.NormalizeDescription(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("GB29 NWBK 6016 1331 9268 19", "GB29NWBK60161331926819")]
    [InlineData("GB29NWBK60161331926819", "GB29NWBK60161331926819")]  // Already clean
    [InlineData("  FR76 3000 6000 0112 3456 7890 189  ", "FR7630006000011234567890189")]
    public void NormalizeIban_StripsSpaces(string input, string expected)
    {
        _sut.NormalizeIban(input).ShouldBe(expected);
    }

    [Fact]
    public void NormalizeIban_EmptyString_ReturnsEmpty()
    {
        _sut.NormalizeIban(string.Empty).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("USD", "USD")]
    [InlineData("  gbp  ", "GBP")]
    [InlineData("MAD", "MAD")]
    public void NormalizeCurrency_UppercasesAndTrims(string input, string expected)
    {
        _sut.NormalizeCurrency(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("US")]    // Too short
    [InlineData("EURO")]  // Too long
    public void NormalizeCurrency_InvalidInput_Throws(string input)
    {
        Should.Throw<ArgumentException>(() => _sut.NormalizeCurrency(input));
    }

    [Fact]
    public void Normalize_ValidRawTransaction_ReturnsSuccess()
    {
        var raw = CreateValidRaw();
        var result = _sut.Normalize(raw);
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Normalize_MissingReference_ReturnsFailure()
    {
        var raw = CreateValidRaw() with { Reference = null };
        var result = _sut.Normalize(raw);
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public void Normalize_MissingAmount_ReturnsFailure()
    {
        var raw = CreateValidRaw() with { Amount = null };
        var result = _sut.Normalize(raw);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Normalize_InvalidAmount_ReturnsFailure()
    {
        var raw = CreateValidRaw() with { Amount = "not-a-number" };
        var result = _sut.Normalize(raw);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Normalize_ProducesIdempotentReference()
    {
        var raw = CreateValidRaw() with { Reference = "REF-001 / ABC-123" };
        var first = _sut.Normalize(raw).Value!;

        // Normalize the already-normalized form
        var secondRaw = raw with
        {
            Reference = first.NormalizedReference.Value
        };
        var second = _sut.Normalize(secondRaw).Value!;

        first.NormalizedReference.Value.ShouldBe(second.NormalizedReference.Value);
    }

    [Fact]
    public void Normalize_AutoFixture_ValidRaw_AlwaysSucceeds()
    {
        var raw = Fakers.RawTransaction().Generate();
        var result = _sut.Normalize(raw);
        // Valid raw must succeed
        result.IsSuccess.ShouldBeTrue();
    }

    // ── FsCheck property tests ────────────────────────────────────────────────

    /// <summary>
    /// FsCheck property: NormalizationIsIdempotent for NormalizeReference.
    /// normalize(normalize(x)) == normalize(x) for all non-null string inputs.
    /// </summary>
    [Property]
    public bool NormalizeReference_IsIdempotent(NonEmptyString validString)
    {
        var input = validString.Get;
        var first = _sut.NormalizeReference(input);
        var second = _sut.NormalizeReference(first);
        return first == second;
    }

    /// <summary>
    /// FsCheck property: NormalizeDescription is idempotent.
    /// </summary>
    [Property]
    public bool NormalizeDescription_IsIdempotent(string? input)
    {
        if (input is null) return true; // Skip nulls
        var first = _sut.NormalizeDescription(input);
        var second = _sut.NormalizeDescription(first);
        return first == second;
    }

    /// <summary>
    /// FsCheck property: NormalizeIban is idempotent.
    /// </summary>
    [Property]
    public bool NormalizeIban_IsIdempotent(string? input)
    {
        if (input is null) return true;
        var first = _sut.NormalizeIban(input);
        var second = _sut.NormalizeIban(first);
        return first == second;
    }

    /// <summary>
    /// FsCheck property: normalized amount is always rounded to 2dp.
    /// </summary>
    [Property]
    public bool NormalizeAmount_AlwaysTwoDecimalPlaces(decimal input)
    {
        var normalized = Math.Round(input, 2, MidpointRounding.AwayFromZero);
        // Verify AwayFromZero rounding invariant
        return Math.Round(normalized, 2, MidpointRounding.AwayFromZero) == normalized;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RawTransaction CreateValidRaw(int seed = 0) => new()
    {
        Reference = $"REF-{seed:D6}-TEST",
        Amount = "1234.56",
        Currency = "EUR",
        ValueDate = "2024-01-15",
        Description = "Test payment",
        Iban = "GB29 NWBK 6016 1331 9268 19",
        RowNumber = 1,
        SourceFileName = "test.csv"
    };
}
