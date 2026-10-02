namespace StudioX.Application;

public sealed record BuildComparisonRow(string Category, string Name, long Before, long After)
{
    public long Difference => After - Before;
}
