namespace StudioX.Engine;

public sealed record Ag32VerilogPort(string Name, string Direction, int Left, int Right)
{
    public bool IsBus => Left != 0 || Right != 0;
    public IEnumerable<string> Signals => IsBus
        ? Enumerable.Range(0, Math.Abs(Left - Right) + 1).Select(i => FormattableString.Invariant($"{Name}[{Left + (Left <= Right ? i : -i)}]"))
        : [Name];
}
