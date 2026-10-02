namespace StudioX.Application.Plugins;

using StudioX.Engine.Debugging;
using StudioX.Extensions.Abstractions;

/// <summary>调试扩展视图的当前有效结果；Panel 只在对应的暂停版本有效。</summary>
public sealed record PluginDebugViewUpdate(long Revision, DebugState State, bool Hardware, string Status,
    PluginPanelDefinition? Panel = null, string? Diagnostic = null);
