namespace StudioX.Desktop;

internal sealed record QuickPickItem(string Label, string Detail, object Value)
{
    public override string ToString() => Label + (Detail.Length == 0 ? "" : "    ·    " + Detail);
}
