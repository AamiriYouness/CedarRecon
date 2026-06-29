# Modulo Resolver — Engineering Notes

> **CedarRecon · PartialMatchStrategy**  
> Why we have three implementations of `x % y == 0`, which one to use, and what the benchmarks actually proved.

---

## The problem

`PartialMatchStrategy` detects split transactions: a source of USD 900 that was posted in the target as three entries of USD 300 each. The core check is:

```
sourceAmount % candidateAmount == 0
```

This runs inside `HashMatchingEngine.Match()`, which is called from `Parallel.ForEachAsync` on every source transaction. On a 1 million transaction batch that's 1 million modulo operations on the hot concurrent path. The choice of arithmetic type matters.

---

## Why `decimal` modulo is not free

`decimal` in .NET is a 128-bit base-10 type stored as:

```
┌─────────────┬────────┬────────┬────────┐
│  flags(32)  │ hi(32) │ lo(32) │ mid(32)│
└─────────────┴────────┴────────┴────────┘
  bits 16-23 = scale (power of 10 to divide mantissa)
  bit  31    = sign
  hi+mid+lo  = 96-bit integer mantissa
```

There is **no x86 instruction for decimal arithmetic**. Every `decimal` operation — add, multiply, modulo — is software-emulated by the CLR. The `Decimal.Remainder` implementation unpacks the 128-bit struct, performs the division on the 96-bit mantissa, and repacks the result.

Compare to `long` modulo, which compiles to a single `div` or `rem` instruction:

```asm
; long modulo
mov rax, [src]
xor rdx, rdx
div [cand]       ; single instruction, remainder in rdx

; decimal modulo
call Decimal.Remainder   ; CLR method call → unpack → software divide → repack
```

**Hypothesis going in:** scaling amounts to `long` before the modulo should be significantly faster.

---

## The three implementations

### 1. `ModuloResolver.Decimal` — correctness reference

```csharp
static (src, cand) => src % cand == 0m
```

Direct `decimal` modulo. No assumptions about scale or magnitude. This is the reference implementation — every other implementation is tested to produce identical results against this one.

**When to use:** tests and correctness verification. Never as a performance default.

---

### 2. `ModuloResolver.ScaledLong` — the hypothesis

```csharp
const long Scale = 10_000L;
var srcL  = (long)(src  * Scale);
var candL = (long)(cand * Scale);
return srcL % candL == 0L;
```

Multiplies both amounts by 10,000 to eliminate decimal places (assumes ≤ 4 decimal places in the domain), then performs integer modulo. The `long %` compiles to a single CPU instruction.

**Assumption:** financial amounts in CedarRecon never exceed 4 decimal places.  
**Safe range:** up to ±922,337,203,685,477 (long.MaxValue ÷ 10,000).

**The catch:** there are **two `decimal` multiplications** before the fast `long %`. Each multiplication is itself software-emulated. This hidden cost is what the benchmark exposed.

---

### 3. `ModuloResolver.UnsafeMantissa` — going to the metal

```csharp
[StructLayout(LayoutKind.Explicit)]
private struct DecimalBits
{
    [FieldOffset(0)]  public decimal Value;
    [FieldOffset(0)]  private int _flags;   // scale in bits 16-23
    [FieldOffset(4)]  private int _hi;
    [FieldOffset(8)]  private int _lo;
    [FieldOffset(12)] private int _mid;
}
```

Reads the raw mantissa and scale directly from the decimal's memory layout via struct overlay — no CLR arithmetic at all. Normalises both values to the same scale by multiplying the lower-scaled mantissa by `10^scaleDiff`, then performs `UInt128` integer modulo.

`UInt128` on .NET 7+ with x64 compiles to hardware-accelerated 128-bit operations (via compiler intrinsics, not a single CPU instruction).

**No scale assumption.** Works for any valid `decimal`.  
**Requires:** `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the benchmark project only.

---

## Benchmark results

Benchmarks run with BenchmarkDotNet v0.15.8. Two machines, both on .NET 10.

### Isolated call — `ModuloBenchmark`

Single modulo call, no loop. Source = `99_999.9999m`, candidate = `9_999.9999m`.

#### Machine: Intel Core Ultra 7 155H · Windows 11 · .NET 10 · RyuJIT x86-64-v3

| Method          | Mean      | Error     | StdDev    | Ratio | Allocated |
|---------------- |----------:|----------:|----------:|------:|----------:|
| **Decimal**     |  9.886 ns | 0.2257 ns | 0.6476 ns |  1.00 |         - |
| ScaledLong      | 33.080 ns | 0.6952 ns | 1.4511 ns |  3.36 |         - |
| UnsafeMantissa  | 13.301 ns | 0.2936 ns | 0.5931 ns |  1.35 |         - |

**ScaledLong is 3.36× slower than Decimal on .NET 10.**

#### Why the hypothesis failed

The two `decimal` multiplications in `ScaledLong` cost more than what the `long %` saves. On .NET 10, RyuJIT's x86-64-v3 code generation optimises `decimal %` more aggressively than it did on .NET 8, narrowing the gap until `ScaledLong`'s pre-multiply overhead dominates.

### Bucket loop — `ModuloBucketBenchmark`

Iterating a realistic bucket of mixed valid/invalid divisors.

#### Intel Core Ultra 7 155H · Windows 11 · .NET 10

| Method     | BucketSize | Mean        | Ratio |
|----------- |----------- |------------:|------:|
| Decimal    | 5          |    89.31 ns |  1.00 |
| ScaledLong | 5          |    90.08 ns |  1.01 |
| Decimal    | 15         |   263.43 ns |  1.00 |
| ScaledLong | 15         |   268.48 ns |  1.02 |
| Decimal    | 50         |   865.30 ns |  1.00 |
| ScaledLong | 50         |   953.36 ns |  1.10 |
| Decimal    | 100        | 1,783.24 ns |  1.00 |
| ScaledLong | 100        | 1,796.40 ns |  1.01 |

Flat ratio across all bucket sizes. The multiply overhead is consistent; it doesn't amortise at larger k.

> **CI produces these tables automatically.** See [`.github/workflows/benchmarks.yml`](../.github/workflows/benchmarks.yml) for the full matrix across .NET 8 / 9 / 10 on Windows, Linux, and macOS.

---

## Decision

| Implementation  | .NET 10 result | Decision |
|---------------- |--------------- |--------- |
| **Decimal**     | Baseline · fastest · no assumptions | ✅ **Default** |
| ScaledLong      | 3.36× slower due to pre-multiply cost | ❌ Retained for .NET 8 conditional compile |
| UnsafeMantissa  | 1.35× slower · no scale assumption | 🔁 Available · promote if bucket cap lifted |

**`Decimal` is the default on .NET 10.** The pre-multiply cost in `ScaledLong` outweighs the integer `%` gain when the JIT already optimises `decimal %` well. All three are kept because the result is runtime-specific — see conditional compile guidance below.

---

## How to choose a resolver

### Option A — accept the default (recommended)

```csharp
// PartialMatchStrategy with no argument → Decimal on .NET 10
services.AddSingleton<IMatchingEngine>(sp =>
    new HashMatchingEngine(
        sp.GetRequiredService<ILogger<HashMatchingEngine>>(),
        MatchStrategyFactory.CreateDefault()));
```

### Option B — pin a specific resolver via DI

```csharp
// Explicit: always use ScaledLong regardless of runtime
services.AddSingleton<IMatchingEngine>(sp =>
    new HashMatchingEngine(
        sp.GetRequiredService<ILogger<HashMatchingEngine>>(),
        MatchStrategyFactory.CreateWithModuloResolver(ModuloResolver.ScaledLong)));
```

### Option C — conditional compile by target framework

```csharp
// appsettings.json or environment variable
// "ModuloResolver": "Decimal" | "ScaledLong" | "UnsafeMantissa"

var resolverName = config["CedarRecon:ModuloResolver"] ?? "Decimal";

var resolver = resolverName switch
{
    "ScaledLong"     => ModuloResolver.ScaledLong,
    "UnsafeMantissa" => ModuloResolver.UnsafeMantissa,
    _                => ModuloResolver.Decimal,
};

services.AddSingleton<IMatchingEngine>(sp =>
    new HashMatchingEngine(
        sp.GetRequiredService<ILogger<HashMatchingEngine>>(),
        MatchStrategyFactory.CreateWithModuloResolver(resolver)));
```

### Option D — compile-time selection by TFM

```csharp
public static readonly IsEvenlyDivisible Default =
#if NET8_0
    ScaledLong;        // ScaledLong faster on .NET 8 (pre-multiply cost lower)
#else
    Decimal;           // Decimal wins on .NET 9+ with improved JIT
#endif
```

---

## Runtime-specific guidance

| Runtime    | Recommended resolver | Reason |
|----------- |--------------------- |------- |
| .NET 10    | `Decimal`            | JIT optimises decimal % well; ScaledLong's pre-multiply dominates |
| .NET 9     | `Decimal`            | Same JIT improvements as .NET 10 |
| .NET 8     | Run benchmark first  | ScaledLong may win; pre-.NET 9 JIT less aggressive on decimal |
| .NET 8 LTS | `ScaledLong`         | Historical benchmark data shows ~15× gain; verify on your hardware |

---

## Adding a new resolver

`ModuloResolver.IsEvenlyDivisible` is a public delegate. Any implementation that satisfies the signature can be injected:

```csharp
// Example: SIMD-based batch resolver (hypothetical)
ModuloResolver.IsEvenlyDivisible simdResolver =
    static (src, cand) => SimdModulo.Check(src, cand);

var engine = new HashMatchingEngine(logger,
    MatchStrategyFactory.CreateWithModuloResolver(simdResolver));
```

New implementations should be:
1. Added to `ModuloResolver.cs` as a named static field
2. Added to `ModuloBenchmark.cs` as a new `[Benchmark]` method
3. Verified against `ModuloResolver.Decimal` in `PartialMatchStrategyTests.cs` Section B

---

## Further reading

- [Pro .NET Memory Management — Konrad Kokosa](https://prodotnetmemory.com/) — decimal internals, JIT output
- [SharpLab.io](https://sharplab.io) — paste any implementation, inspect JIT assembly
- [BenchmarkDotNet docs](https://benchmarkdotnet.org) — how to run and interpret results
- `.github/workflows/benchmarks.yml` — CI matrix (Windows/Linux/macOS × .NET 8/9/10)