namespace StudioX.Application.Plugins;

using System.Text.Json;

/// <summary>主机操作完成通知，供界面按工程代际同步已打开文件。</summary>
public sealed record PluginHostOperation(string PluginId, string Tool, JsonElement Arguments, JsonElement Result);
