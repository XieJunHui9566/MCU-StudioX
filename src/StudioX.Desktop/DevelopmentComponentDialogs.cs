namespace StudioX.Desktop;

using StudioX.Engine;

internal static class DevelopmentComponentDialogs
{
    internal static string ImportFilter => $"MCU 开发环境组件|*{ToolchainArchiveFormat.Extension};*{ToolchainArchiveFormat.LegacyExtension}|MCU 开发环境组件组件|*{ToolchainArchiveFormat.Extension}|已有离线工具包|*{ToolchainArchiveFormat.LegacyExtension}";
    internal static string ExportFilter => $"MCU 开发环境组件组件|*{ToolchainArchiveFormat.Extension}|已有离线工具包|*{ToolchainArchiveFormat.LegacyExtension}";
}
