using BeyondAsyncAwait.Act02;
using BeyondAsyncAwait.Shared;

return await DemoHost.RunAsync(args,
    new CacheStampedeDemo(),
    new FailureCachingDemo(),
    new SharedCancellationDemo(),
    new FactoryContextDemo(),
    new HybridCacheDemo());
