namespace StudioX.Application.Plugins;

using StudioX.Extensions;
using StudioX.Extensions.Abstractions;

/// <summary>单个已激活插件的固定贡献定义；会话内 Agent 工具描述保持不变。</summary>
public sealed record PluginActiveContribution(string Id, PluginManifest Manifest, PluginContribution Contribution);
