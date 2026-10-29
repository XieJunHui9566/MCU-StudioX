namespace StudioX.Extensions.Abstractions;

/// <summary>时钟关系与插件计算的显示值，不表示硬件实测。</summary>
public sealed record PluginMcuClock(string Id, string Name, string[] InputIds, double X, double Y, string Value,
    string Kind = "block", string Symbol = "", string Detail = "", bool Enabled = true, string[]? ActiveInputIds = null);
