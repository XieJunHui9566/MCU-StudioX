namespace StudioX.Engine.Hdl;

/// <summary>一次源码快照的逻辑预览；单元数不表示 AGM 布局后的 LE 占用或时序。</summary>
public sealed record HdlSchematicResult(string ProjectDirectory, string TopModule, HdlModule[] Modules,
    string SourceDigest, IReadOnlyDictionary<string, string> InputHashes,
    string NetlistPath, string LogPath, string ToolVersion, DateTimeOffset CreatedAt,
    HdlSchematicSettings Settings, string[] Warnings);
