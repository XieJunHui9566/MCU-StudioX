namespace StudioX.Packages;

/// <summary>普通 MCU 模板勾选特殊模式后启用的逻辑资源和构建增量。</summary>
public sealed record Ag32LogicTemplate(string PinMapFile, string VerilogFile,
    string? EntryFile = null, TemplateBuild? Build = null);
