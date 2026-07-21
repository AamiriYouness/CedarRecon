using CedarRecon.Indexing;
using Shouldly;

namespace CedarRecon.Tests.Unit.Indexing;

/// <summary>
/// Edge cases for MoneyMinorUnitsConverter — scale 10^4 (4 decimal places),
/// deliberately more than the 2dp most currencies need (see the converter's
/// own doc comment: KWD/BHD/OMR use 3dp, upstream FX rounding residue can
/// add a 4th). Throws rather than silently rounds on >4dp input.
/// </summary>
public class MoneyMinorUnitsConverterTests
{
    [Fact]
    public void ToMinorUnits_ExactlyFourDecimalPlaces_RoundTrips()
    {
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(123.4567m);

        minor.ShouldBe(1_234_567L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(123.4567m);
    }

    [Fact]
    public void ToMinorUnits_MoreThanFourDecimalPlaces_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MoneyMinorUnitsConverter.ToMinorUnits(1.23456m));
    }

    [Fact]
    public void ToMinorUnits_NegativeAmount_RoundTrips()
    {
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(-500.25m);

        minor.ShouldBe(-5_002_500L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(-500.25m);
    }

    [Fact]
    public void ToMinorUnits_NegativeAmount_MoreThanFourDecimalPlaces_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MoneyMinorUnitsConverter.ToMinorUnits(-1.23456m));
    }

    [Fact]
    public void ToMinorUnits_Zero_RoundTrips()
    {
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(0m);

        minor.ShouldBe(0L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(0m);
    }

    [Fact]
    public void ToMinorUnits_WholeNumber_RoundTrips()
    {
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(1000m);

        minor.ShouldBe(10_000_000L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(1000m);
    }

    [Fact]
    public void ToMinorUnits_OneDecimalPlace_RoundTrips()
    {
        // Fewer than 4dp — should NOT throw, trailing zeros are implicit.
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(9.5m);

        minor.ShouldBe(95_000L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(9.5m);
    }

    [Fact]
    public void ToMinorUnits_LargeButRealisticAmount_DoesNotOverflow()
    {
        // Comfortably within long range at scale 10^4 (long max is
        // ~922 trillion at this scale — far beyond any plausible
        // reconciliation transaction amount, per the converter's own
        // doc comment). Using a large-but-realistic value rather than
        // decimal.MaxValue, which would overflow long even before the
        // >4dp check could reject it.
        const decimal largeAmount = 999_999_999_999.9999m;

        var minor = MoneyMinorUnitsConverter.ToMinorUnits(largeAmount);

        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(largeAmount);
    }

    [Fact]
    public void ToMinorUnits_SmallestNonZeroUnit_RoundTrips()
    {
        var minor = MoneyMinorUnitsConverter.ToMinorUnits(0.0001m);

        minor.ShouldBe(1L);
        MoneyMinorUnitsConverter.ToAmount(minor).ShouldBe(0.0001m);
    }

    [Fact]
    public void ToMinorUnits_ExceedsLongRangeAtScale_ThrowsOverflowNotSilentWraparound()
    {
        // long.MaxValue / 10^4 ≈ 922,337,203,685,477.58 — an amount just
        // past that, at scale 10^4, exceeds long range on the *ScaleFactor
        // multiply. The converter's own doc comment promises "an explicit
        // OverflowException on the rare pathological input is preferable
        // to silent wraparound" — this proves that guarantee holds rather
        // than assuming it.
        const decimal justOverLongRange = 922_337_203_685_478m;

        Should.Throw<OverflowException>(() =>
            MoneyMinorUnitsConverter.ToMinorUnits(justOverLongRange));
    }
}
