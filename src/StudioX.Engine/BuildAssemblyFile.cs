namespace StudioX.Engine;

using System.Text.Json.Serialization;

public sealed record BuildAssemblyFile(string RelativePath, string Sha256, string? SourcePath = null)
{
    [JsonIgnore]
    public string Kind => Path.GetExtension(RelativePath).ToLowerInvariant() switch
    {
        ".rst" => "链接汇编 · 地址 / 机器码",
        ".lst" => "汇编清单 · 链接前地址",
        _ => "编译器汇编 · 链接前"
    };
    [JsonIgnore]
    public string DisplayName => (SourcePath ?? Path.GetFileNameWithoutExtension(RelativePath)) + " · " + Kind;
}
