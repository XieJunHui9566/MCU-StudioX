namespace StudioX.Application.PeripheralDevelopment;

using StudioX.Engine;

/// <summary>预览绑定工程和 SDK 证据；应用前重新读取，防止版本切换后插入旧 API。</summary>
public sealed record PeripheralDevelopmentContext(string ProjectDirectory, ProjectManifest Project, string SdkDirectory,
    string EvidenceStamp, int GpioCount, IReadOnlyList<PeripheralOption> Options);
