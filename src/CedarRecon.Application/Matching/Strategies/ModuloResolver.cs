using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CedarRecon.Application.Matching.Strategies;

/// <summary>
/// Pluggable modulo implementations for IsPartialSplit.
///
/// Three implementations, each a trade-off:
///
///   Decimal  — correctness reference, no assumptions on scale.
///              Uses CLR software-emulated arithmetic. ~48 ns per call.
///
///   ScaledLong — scales amounts to long (×10_000), uses a single CPU div instruction.
///                Assumes max 4 decimal places. ~3 ns per call. ← default in production.
///
///
/// Benchmark results (BenchmarkDotNet, .NET 10, x64):
///   | Method          | Mean    | Ratio |
///   |---------------- |--------:|------:|
///   | Decimal         | 48.3 ns |  1.0x |
///   | ScaledLong      |  3.1 ns | 15.6x |
/// </summary>
public static class ModuloResolver
{
    /// <summary>
    /// Returns true if candidateAmount is a clean integer fraction of sourceAmount.
    /// Both amounts are absolute values — sign handling is the caller's concern.
    /// </summary>
    public delegate bool IsEvenlyDivisible(decimal sourceAmount, decimal candidateAmount);

    /// <summary>
    /// Correctness reference. No scale assumptions.
    /// Slow — CLR software-emulates decimal arithmetic, no hardware instruction.
    /// Use in tests to verify other implementations produce identical results.
    /// </summary>
    public static readonly IsEvenlyDivisible Decimal =
        static (src, cand) => src % cand == 0m;

    /// <summary>
    /// Scales both amounts to long (×10_000) before applying modulo.
    /// Compiles to a single x86 `div` instruction — ~15x faster than decimal.
    ///
    /// Assumes amounts have at most 4 decimal places.
    /// Safe up to ±922_337_203_685_477 (long.MaxValue / 10_000).
    /// </summary>
    public static readonly IsEvenlyDivisible ScaledLong = static (src, cand) =>
    {
        // decimal modulo is software-emulated — no hardware instruction exists for it.
        // Financial amounts are bounded to 4 decimal places in this domain,
        // so we scale to long and use a single CPU div instruction instead.
        // ~15x faster on the hot Match() path. See Benchmarks/ModuloBenchmark.cs.
        const long Scale = 10_000L;
        var srcL = (long)(src * Scale);
        var candL = (long)(cand * Scale);
        return srcL % candL == 0L;
    };

    /// <summary>
    /// Reads the raw decimal mantissa via struct overlay, normalises both values
    /// to the same scale, then performs UInt128 integer modulo.
    ///
    /// No decimal place assumption. Works for any valid decimal value.
    /// Requires AllowUnsafeBlocks in csproj.
    /// ~20% faster than ScaledLong — marginal for bucket sizes ≤ 15.
    /// Retain for when the bucket cap is lifted or batch sizes increase.
    /// </summary>
    public static readonly IsEvenlyDivisible UnsafeMantissa =
        static (src, cand) => UnsafeMantissaImpl(src, cand);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UnsafeMantissaImpl(decimal src, decimal cand)
    {
        var srcBits = DecimalBits.From(src);
        var candBits = DecimalBits.From(cand);

        var srcM = srcBits.Mantissa;
        var candM = candBits.Mantissa;

        // Normalise to same scale before comparing mantissas
        var scaleDiff = srcBits.Scale - candBits.Scale;
        if (scaleDiff > 0)
            candM *= Pow10((uint)scaleDiff);
        else if (scaleDiff < 0)
            srcM *= Pow10((uint)(-scaleDiff));

        return srcM % candM == 0;
    }

    // ── DecimalBits overlay ───────────────────────────────────────────────────

    /// <summary>
    /// Overlays a decimal's memory layout to extract mantissa and scale
    /// without going through CLR decimal arithmetic.
    ///
    /// .NET decimal memory layout (little-endian):
    ///   Offset 0:  int flags  — bits 16-23 = scale, bit 31 = sign
    ///   Offset 4:  int hi     — high 32 bits of 96-bit mantissa
    ///   Offset 8:  int lo     — low 32 bits of 96-bit mantissa
    ///   Offset 12: int mid    — middle 32 bits of 96-bit mantissa
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct DecimalBits
    {
        [FieldOffset(0)] public decimal Value;
        [FieldOffset(0)] private int _flags;
        [FieldOffset(4)] private int _hi;
        [FieldOffset(8)] private int _lo;
        [FieldOffset(12)] private int _mid;

        public int Scale => (_flags >> 16) & 0x7F;

        /// <summary>96-bit mantissa reconstructed as UInt128.</summary>
        public UInt128 Mantissa =>
            ((UInt128)(uint)_hi << 64) |
            ((UInt128)(uint)_mid << 32) |
            (uint)_lo;

        public static DecimalBits From(decimal value) =>
            new() { Value = Math.Abs(value) };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 Pow10(uint exp)
    {
        UInt128 result = 1;
        for (var i = 0u; i < exp; i++) result *= 10;
        return result;
    }
}
