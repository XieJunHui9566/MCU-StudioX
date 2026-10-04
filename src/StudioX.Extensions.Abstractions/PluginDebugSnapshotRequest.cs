namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>API 3 只读调试输入。Revision 标识本次视图刷新，不是设备时钟。</summary>
public sealed record PluginDebugSnapshotRequest(string State, bool Hardware, JsonElement Snapshot)
{
    public int FormatVersion { get; init; } = 1;
    public long Revision
    {
        get; init;
    }
    public string Reason { get; init; } = "";
}
