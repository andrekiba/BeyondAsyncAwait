using BeyondAsyncAwait.RuntimeAsyncDemos;
using BeyondAsyncAwait.Shared;

// Run the same demo in both projects and compare:
//   dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Classic -- il
//   dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Native  -- il
return await DemoHost.RunAsync(args,
    new IlDemo(),
    new StackTraceDemo(),
    new ElisionDemo());
