namespace StudioX.Application.Lvgl;

/// <summary>旧版外置编译器配置不会隐式授权新版本继续运行机器上的外部工具。</summary>
public sealed record LvglPreviewToolchainSettings(int FormatVersion, string Mode, StudioX.Engine.Lvgl.LvglHostToolchain? Toolchain = null);
