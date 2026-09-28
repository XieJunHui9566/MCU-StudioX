namespace StudioX.Engine.Hdl;

/// <summary>只有源码、工具和各项产物都匹配的构建才能进入联合下载。</summary>
internal sealed record Ag32NativeBuildReceipt(int FormatVersion, Ag32NativeBuildSettings Settings,
    Dictionary<string, string> Inputs, Dictionary<string, string> Artifacts, string MappingTools,
    string NativeTools, string ImageRelativePath, string SourceDigest)
{
    internal const string RelativePath = ".build/ag32-logic/receipt.json";
}
