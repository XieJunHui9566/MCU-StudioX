namespace StudioX.Extensions.Abstractions;

/// <summary>由应用渲染的纯数据面板，不能包含可执行 XAML。</summary>
public sealed record PluginPanelDefinition(string Id, string Title, PluginPanelWidget[] Widgets)
{
    /// <summary>保留三参数构造函数的二进制契约；window 由声明 panelWindows 能力的插件显式选择。</summary>
    public string Presentation { get; init; } = "panel";
}
