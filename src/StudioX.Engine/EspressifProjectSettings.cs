namespace StudioX.Engine;

/// <summary>工程只锁定 SDK 身份与目标，共享 SDK 路径由内置工具服务解析。</summary>
public sealed record EspressifProjectSettings(string Framework, string Target, string SdkVersion, int FormatVersion = 1);
