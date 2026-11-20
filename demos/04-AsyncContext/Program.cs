using BeyondAsyncAwait.Demo04;
using BeyondAsyncAwait.Shared;

return await DemoHost.RunAsync(args,
    new AsyncLocalFlowDemo(),
    new ContextLeakDemo(),
    new AsyncDisposeDemo());
