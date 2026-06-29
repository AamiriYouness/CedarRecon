using BenchmarkDotNet.Running;

// BenchmarkDotNet entry point.
//
// LOCAL USAGE:
//   dotnet run -c Release                          ← runs all benchmarks
//   dotnet run -c Release -- --filter *Modulo*     ← filter by name
//   dotnet run -c Release -- --list flat           ← list all available benchmarks
//   dotnet run -c Release -- --job short           ← faster run, less accuracy
//
// CI USAGE (automatic — see .github/workflows/ci.yml):
//   Main branch  → benchmarks job runs all benchmarks, uploads artifacts
//   Manual trigger → benchmark-matrix job runs 3 OS × 3 runtimes in parallel
//
// CAN BENCHMARKS BE PARALLELISED?
//   No — and deliberately so. BenchmarkDotNet runs each benchmark method in
//   isolation with JIT warmup, GC collection, and CPU affinity controls.
//   Running multiple benchmarks in parallel would pollute each other's results
//   through cache thrashing, CPU contention, and GC pressure.
//
//   What IS parallel: the CI matrix jobs. Each OS/runtime combination runs on
//   its own GitHub Actions runner — fully isolated machines, not threads.
//   That's the right level of parallelism for cross-platform comparison.
//
// ADDING A NEW BENCHMARK:
//   1. Create a class with [MemoryDiagnoser] and [Benchmark] methods
//   2. BenchmarkSwitcher.FromAssembly discovers it automatically
//   3. No registration needed here

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
