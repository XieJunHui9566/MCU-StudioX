namespace StudioX.Application;

public sealed record ToolEnvironmentEntry(string Id, string Version, string Name, string CompilerId, long Bytes, int Files, bool Required, string Status, string Components = "")
{
    public string SizeText => Bytes >= 1073741824 ? $"{Bytes / 1073741824d:F2} GiB" : $"{Bytes / 1048576d:F1} MiB";
    public string RequiredText => Required ? "工程锁定" : "可用";
}
