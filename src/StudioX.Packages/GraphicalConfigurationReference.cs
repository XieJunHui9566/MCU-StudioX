namespace StudioX.Packages;

/// <summary>工程主配置的明确关联，不在工程清单中复制用户参数。</summary>
public sealed record GraphicalConfigurationReference(string Path = "device.mcuconfig");
