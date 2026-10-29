using AiInfra.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// Two jobs per benchmark, as fixed in docs/07-benchmarking.md: JIT with tiered PGO, and Native AOT.
// BenchmarkDotNet 0.16 (preview) is the first release that knows the .NET 11 runtime monikers.
// Pass "--job jit" or "--job aot" to run only one of them.
bool runJit = !args.Contains("aot");
bool runAot = !args.Contains("jit");
args = args.Where(a => a is not ("--job" or "jit" or "aot")).ToArray();

var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddExporter(JsonExporter.Full)
    .WithArtifactsPath(Path.Combine(Datasets.RepoRoot, "bench", "results", "csharp"));

if (runJit)
    config.AddJob(Job.Default.WithId("JIT").WithRuntime(CoreRuntime.Core11_0));
if (runAot)
    config.AddJob(Job.Default.WithId("NativeAOT").WithRuntime(NativeAotRuntime.Net11_0));

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
