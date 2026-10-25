using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// Every benchmark runs twice, from the same source code:
//   StateMachine  -> built with -p:RuntimeAsync=false (compiler-generated state machines)
//   RuntimeAsync  -> built with -p:RuntimeAsync=true  (runtime-async=on, see Directory.Build.targets)
// The Ratio column is RuntimeAsync vs StateMachine.
//
//   dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter *AwaitChain*
//   add --quick for a short run (fewer iterations, less precise)

var quick = args.Contains("--quick");
var job = quick ? Job.ShortRun : Job.Default;

var config = DefaultConfig.Instance
    .AddJob(job.WithArguments([new MsBuildArgument("/p:RuntimeAsync=false")]).WithId("StateMachine").AsBaseline())
    .AddJob(job.WithArguments([new MsBuildArgument("/p:RuntimeAsync=true")]).WithId("RuntimeAsync"))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .WithOptions(ConfigOptions.JoinSummary);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args.Where(a => a != "--quick").ToArray(), config);
