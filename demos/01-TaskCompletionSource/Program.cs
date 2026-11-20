using BeyondAsyncAwait.Demo01;
using BeyondAsyncAwait.Shared;

return await DemoHost.RunAsync(args,
    new InlineContinuationsDemo(),
    new RpcCorrelationDemo(),
    new ManualResetEventDemo());
