namespace StudioX.Engine.Lvgl;

/// <summary>共享 UI 源文件的 PC 构建入口；编译器路径仅保存在机器设置中。</summary>
public sealed record LvglPreviewConfiguration(int FormatVersion, string LvglDirectory, string ConfigurationHeader,
    IReadOnlyList<string> SourceFiles, IReadOnlyList<string> IncludeDirectories, string EntryPoint,
    int Width = 240, int Height = 320, int DrawBufferRows = 10, int Zoom = 2, int ColorDepth = 16,
    bool AutoRebuild = true, int DrawBufferCount = 1, IReadOnlyList<string>? SourceExclusions = null,
    int TargetFramesPerSecond = 30, long? DisplayBandwidthBytesPerSecond = null, string? DisplayBandwidthSource = null,
    IReadOnlyList<string>? ResourceDirectories = null, string? UiDirectory = null);
