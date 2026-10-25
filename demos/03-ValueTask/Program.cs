using BeyondAsyncAwait.Act03;
using BeyondAsyncAwait.Shared;

// Allocation numbers are meaningful in Release:
//   dotnet run -c Release --project demos/03-ValueTask -- 3.1
//   dotnet run -c Release --project demos/03-ValueTask -p:RuntimeAsync=false -- 3.1
return await DemoHost.RunAsync(args,
    new CacheHitDemo(),
    new ValueTaskSourceDemo());
