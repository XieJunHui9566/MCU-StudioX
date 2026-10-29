namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>图形配置保存候选；只有宿主收到用户的保存操作才提交工程文件。</summary>
public sealed record PluginMcuSavePlan(int FormatVersion, string OriginalSha256, JsonElement Configuration);
