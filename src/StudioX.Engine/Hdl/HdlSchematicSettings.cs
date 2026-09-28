namespace StudioX.Engine.Hdl;

/// <summary>工程内的 HDL 预览输入；所有路径相对于工程，不携带开发机工具路径。</summary>
public sealed record HdlSchematicSettings(int FormatVersion, string[] Sources, string[] IncludeDirectories,
    string[] Defines, string TopModule = "", bool Flatten = false);
