namespace StudioX.Extensions.Abstractions;

/// <summary>由应用渲染的纯数据面板，不能包含可执行 XAML。</summary>
public sealed record PluginPanelDefinition(string Id, string Title, PluginPanelWidget[] Widgets);
