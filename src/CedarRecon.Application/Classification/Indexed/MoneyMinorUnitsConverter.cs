namespace CedarRecon.Application.Classification.Indexed;

/// <summary>
/// Converts between decimal Money.Amount and the compact long AmountMinor
/// representation used by IndexedTransaction (worklist item 1, compaction
/// phase — see docs context: decimal Math.Abs is software-emulated, long
/// Math.Abs is a single CPU instruction; this directly targets the
/// MismatchScan regression observed in IndexedClassifierPhaseBenchmark).
///
/// SCALE FACTOR: 10,000 (4 decimal places), NOT 2.
///
/// Why 4 and not 2:
/// Reconciliation input is multi-currency (Money carries a Currency string
/// but no per-currency precision metadata at this layer). Most currencies
/// use 2 decimal places, but ISO 4217 currencies like KWD/BHD/OMR use 3, and
/// upstream feeds occasionally carry sub-cent rounding residue from FX or
/// fee calculations. Scaling by 10^2 would silently truncate any of that —
/// not a rounding choice, a SILENT DATA LOSS bug, and one that would only
/// surface on whatever dataset happens to contain a 3-decimal-place currency
/// or residual digit, i.e. invisible in 2dp-only test fixtures. 10^4 is a
/// strict superset of every realistic precision in this domain, so the
/// extra two digits of headroom cost nothing (still comfortably inside long
/// range for any realistic transaction amount) and remove the silent-loss
/// failure mode entirely.
///
/// This is an INDEPENDENT decision from docs/modulo-resolver.md's
/// ScaledLong investigation — that document concluded ScaledLong was
/// REJECTED there because pre-multiply cost made it 3.36x SLOWER than
/// Decimal for the modulo-resolution use case specifically. That rejection
/// does not transfer here: this conversion happens ONCE per row at index-build
/// time (not repeatedly inside a hot comparison loop the way ModuloResolver's
/// scaling was), and the benefit being targeted here is comparison/Math.Abs
/// cost in MismatchScan, not modulo arithmetic cost. Re-verified independently
/// per the worklist instruction — do not assume the two conclusions conflict.
///
/// Round-trip is exact for any decimal value representable with &lt;= 4 decimal
/// places (every realistic Money.Amount in this domain). Values with more
/// than 4 decimal places are rejected, not silently rounded — silent rounding
/// of money amounts is a correctness bug, not an acceptable precision loss,
/// so callers must be explicit if that ever needs to change.
/// </summary>
public static class MoneyMinorUnitsConverter
{
    /// <summary>
    /// Number of decimal places preserved. AmountMinor = Amount * 10^Scale.
    /// </summary>
    public const int Scale = 4;

    private const decimal ScaleFactor = 10_000m; // 10^Scale — kept in sync manually with Scale, see test that asserts this

    /// <summary>
    /// Converts a decimal Money.Amount to its long minor-units representation.
    /// Throws if the input carries more precision than Scale supports, since
    /// silently rounding a money amount is never acceptable here.
    /// </summary>
    public static long ToMinorUnits(decimal amount)
    {
        var scaled = amount * ScaleFactor;
        var rounded = decimal.Truncate(scaled);

        if (scaled != rounded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"Amount has more than {Scale} decimal places and cannot be represented " +
                "exactly as minor units without silent precision loss. This indicates either " +
                "unexpected upstream data or that Scale needs to be increased — it must not be " +
                "silently rounded.");
        }

        // decimal -> long: safe for any realistic transaction amount (long
        // range is +/-922 trillion at this scale, far beyond any plausible
        // reconciliation amount); an explicit OverflowException on the rare
        // pathological input is preferable to silent wraparound.
        return (long)rounded;
    }

    /// <summary>
    /// Converts a long minor-units value back to decimal Money.Amount.
    /// Exact inverse of ToMinorUnits for any value it produced.
    /// </summary>
    public static decimal ToAmount(long amountMinor) => amountMinor / ScaleFactor;
}
