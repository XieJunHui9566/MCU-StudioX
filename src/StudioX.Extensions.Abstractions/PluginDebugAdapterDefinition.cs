namespace StudioX.Extensions.Abstractions;

/// <summary>API 3 调试快照适配器；解释宿主快照，不持有设备、不隐式启动会话。</summary>
public sealed record PluginDebugAdapterDefinition(string Id, string DisplayName);
