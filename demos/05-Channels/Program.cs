using BeyondAsyncAwait.Act05;
using BeyondAsyncAwait.Shared;

return await DemoHost.RunAsync(args,
    new BackpressureDemo(),
    new PipelineDemo(),
    new WhenEachDemo());
