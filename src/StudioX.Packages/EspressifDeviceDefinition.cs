namespace StudioX.Packages;

/// <summary>由原生 SDK 管理的构建目标；不把外置 Flash 或 SDK 可用堆猜成固定裸机内存。</summary>
public sealed record EspressifDeviceDefinition(string Framework, string Target, string SdkVersion);
