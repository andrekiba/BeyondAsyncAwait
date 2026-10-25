namespace BeyondAsyncAwait.Shared;

public enum Variant
{
    Broken,
    Fixed
}

/// <summary>
/// One demo of the session. Most demos run a <see cref="Variant.Broken"/> version first
/// (the bug, running, with a number on screen) and then the <see cref="Variant.Fixed"/> one.
/// </summary>
public abstract class Demo
{
    /// <summary>Id as in the session outline, e.g. "1.2".</summary>
    public abstract string Id { get; }

    /// <summary>Short kebab-case name usable from the command line, e.g. "rpc-correlation".</summary>
    public abstract string Name { get; }

    public abstract string Title { get; }

    /// <summary>False for demos that only show something (no broken/fixed pair).</summary>
    public virtual bool HasVariants => true;

    public abstract Task RunAsync(Variant variant);
}
