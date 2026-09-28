namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>版本 2 JSON 行协议；双向请求使用各自生成的 RequestId 配对响应。</summary>
public sealed record PluginProtocolMessage(
    int ProtocolVersion,
    string Kind,
    string? RequestId = null,
    string? Method = null,
    JsonElement? Payload = null,
    string? ErrorCode = null,
    string? Error = null);
