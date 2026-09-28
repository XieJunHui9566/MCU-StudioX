namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>面板控件数据；Kind 限定为宿主认识的控件，不加载插件界面程序集。</summary>
public sealed record PluginPanelWidget(
    string Id,
    string Kind,
    string Label,
    JsonElement? Value = null,
    string? CommandId = null,
    string[]? Columns = null,
    PluginPanelWidget[]? Children = null);
