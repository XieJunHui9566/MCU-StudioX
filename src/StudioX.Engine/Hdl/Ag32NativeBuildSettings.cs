namespace StudioX.Engine.Hdl;

/// <summary>自定义逻辑的真实构建输入，与电路图所选择的观察模块独立保存。</summary>
public sealed record Ag32NativeBuildSettings(int FormatVersion, string[] Sources, string[] IncludeDirectories,
    string[] Defines, string[] SdcFiles)
{
    public const string RelativePath = ".studiox/ag32-logic-build.json";
    internal HdlSchematicSettings Inputs => new(1, Sources, IncludeDirectories, Defines, "user_logic");
}
