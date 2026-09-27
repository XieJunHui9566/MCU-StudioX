namespace StudioX.Engine;

/// <summary>来自 SDK 实际构建布局的单个 Flash 映像，不推测各芯片的固定偏移。</summary>
public sealed record EspressifFlashImage(uint Offset, string RelativePath, string Sha256, long Bytes);
