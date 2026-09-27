namespace StudioX.Engine;

using System.Text.Json.Serialization;

/// <summary>用户声明的模块存储规格；空值继续使用工程的原生 SDK 配置，不代表硬件探测。</summary>
public sealed record EspressifModuleSettings(int FormatVersion = 1, string? ProfileId = null,
    int? FlashSizeMb = null, string? FlashMode = null, int? FlashFrequencyMhz = null,
    string? PsramMode = null, int? PsramSizeMb = null, string? P4RevisionFamily = null, bool? SingleCore = null)
{
    public const string RelativePath = ".studiox/espressif-module.json";
    internal const string GeneratedConfigPath = ".build/studiox-module-sdkconfig";
    [JsonIgnore]
    public bool HasOverrides => FlashSizeMb is not null || FlashMode is not null || FlashFrequencyMhz is not null ||
        PsramMode is not null || PsramSizeMb is not null || P4RevisionFamily is not null || SingleCore is not null;
}
