namespace StudioX.Engine;

/// <summary>经构建回执、源码指纹和产物哈希核对的 SDCC 调试数据；附加时仍须核对目标 CODE。</summary>
public sealed record StcDebugArtifact(string ProjectDirectory, string ImagePath, string SymbolsPath, string SymbolsText,
    byte[] Code, bool[] Present, string ImageSha256, string SymbolsSha256, string SourceStamp, string[]? SourceFiles = null);
