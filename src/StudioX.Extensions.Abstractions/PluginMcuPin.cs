namespace StudioX.Extensions.Abstractions;

/// <summary>物理焊盘及已核对功能，坐标仅用于封装示意。</summary>
public sealed record PluginMcuPin(string Id, string Position, string Name, string Category,
    double X, double Y, string Side, PluginMcuOption[] Functions, string FunctionId, string Label)
{
    public bool Locked
    {
        get; init;
    }
    public bool CanEditMetadata
    {
        get; init;
    }
}
