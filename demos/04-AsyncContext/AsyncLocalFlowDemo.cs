using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act04;

/// <summary>
/// 4.1 — AsyncLocal flows down, never back up. An async method runs on a copy-on-write ExecutionContext:
/// whatever it sets is gone when it returns to its caller (even before its first await). A sync method has no
/// such boundary. IHttpContextAccessor stores a mutable holder in its AsyncLocal for the same reason: mutating
/// the holder is visible to every copy of the context. Same semantics with or without runtime async:
/// dotnet run -- 4.1   vs   dotnet run -p:RuntimeAsync=false -- 4.1
/// </summary>
sealed class AsyncLocalFlowDemo : Demo
{
    static readonly AsyncLocal<string?> Tenant = new();
    static readonly AsyncLocal<TenantHolder?> Holder = new();

    public override string Id => "4.1";
    public override string Name => "asynclocal-flow";
    public override string Title => "AsyncLocal does not flow back to the caller";

    public override async Task RunAsync(Variant variant)
    {
        Out.Info(BuildInfo.Description);

        if (variant == Variant.Broken)
        {
            Tenant.Value = null;
            await SetTenantAsync("acme");
            Out.Metric("after await SetTenantAsync()", Tenant.Value ?? "(null)", ok: false);

            Tenant.Value = null;
            await SetTenantBeforeAnyAwaitAsync("acme");
            Out.Metric("set before its first await", Tenant.Value ?? "(null)", ok: false);
        }
        else
        {
            Tenant.Value = null;
            SetTenant("acme");
            Out.Metric("after SetTenant() (sync)", Tenant.Value ?? "(null)", ok: Tenant.Value == "acme");

            Tenant.Value = null;
            Tenant.Value = await LookupTenantAsync("acme");
            Out.Metric("caller assigns the result", Tenant.Value ?? "(null)", ok: Tenant.Value == "acme");

            Holder.Value = new TenantHolder(); // set once, at the start of the request
            await ResolveIntoHolderAsync("acme");
            Out.Metric("holder mutated by async code", Holder.Value?.Name ?? "(null)", ok: Holder.Value?.Name == "acme");
        }
    }

    static async Task SetTenantAsync(string tenant)
    {
        await Task.Delay(10); // e.g. a lookup
        Tenant.Value = tenant;
        Out.Log($"inside SetTenantAsync: tenant = {Tenant.Value}");
    }

    static async Task SetTenantBeforeAnyAwaitAsync(string tenant)
    {
        Tenant.Value = tenant;
        Out.Log($"inside SetTenantBeforeAnyAwaitAsync: tenant = {Tenant.Value}");
        await Task.Delay(10);
    }

    static void SetTenant(string tenant) => Tenant.Value = tenant;

    static async Task<string> LookupTenantAsync(string tenant)
    {
        await Task.Delay(10);
        return tenant;
    }

    static async Task ResolveIntoHolderAsync(string tenant)
    {
        await Task.Delay(10);
        Holder.Value!.Name = tenant; // mutate the shared object, don't replace the AsyncLocal value
    }

    sealed class TenantHolder
    {
        public string? Name { get; set; }
    }
}
