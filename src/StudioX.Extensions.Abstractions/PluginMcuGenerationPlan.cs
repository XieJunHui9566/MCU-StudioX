namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>用户点击生成后返回的纯数据计划；磁盘提交由应用服务验证和执行。</summary>
public sealed record PluginMcuGenerationPlan(int FormatVersion, string OriginalSha256,
    JsonElement Configuration, PluginMcuGeneratedFile[] Files);
