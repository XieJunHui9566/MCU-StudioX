namespace StudioX.Engine;

public sealed record DevelopmentComponentLock(int FormatVersion, IReadOnlyList<DevelopmentComponentPin> Components)
{
    public const string RelativePath = ".studiox/development-components.lock.json";
}
