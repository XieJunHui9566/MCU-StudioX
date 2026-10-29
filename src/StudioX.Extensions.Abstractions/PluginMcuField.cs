namespace StudioX.Extensions.Abstractions;

/// <summary>按节分组的有界参数；宿主仅向插件提交字段 ID 和输入值。</summary>
public sealed record PluginMcuField(string Id, string Section, string Label, string Kind,
    string Value, PluginMcuOption[] Options, string Page = "peripherals", string ClockNodeId = "");
