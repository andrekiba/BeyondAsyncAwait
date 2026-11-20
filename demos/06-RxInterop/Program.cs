using BeyondAsyncAwait.Demo06;
using BeyondAsyncAwait.Shared;

return await DemoHost.RunAsync(args,
    new TaskObservableDemo(),
    new AsyncInPipelineDemo(),
    new RxToChannelDemo(),
    new RefreshingCacheDemo(),
    new AsyncRxBackpressureDemo(),
    new AsyncRxBridgeDemo(),
    new AsyncRxLifecycleDemo());
