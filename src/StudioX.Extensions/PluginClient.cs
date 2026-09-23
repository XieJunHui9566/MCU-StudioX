namespace StudioX.Extensions;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>首版每次解码启动短生命周期宿主；后续高频流用持久会话协议替换。</summary>
public sealed class PluginClient(string hostExecutable)
{
    public async Task<DecodeResult> DecodeAsync(string manifestPath, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > 65536) throw new StudioXException("PLUGIN_INPUT_LIMIT", "单次解码最多 64 KiB。");
        _ = await PluginManifest.ReadAsync(manifestPath, cancellationToken);
        var request = new DecoderRequest(1, Guid.NewGuid().ToString("N"), Convert.ToBase64String(payload.Span));
        var response = await new ProcessRunner().RunAsync(new ProcessRequest(Path.GetFullPath(hostExecutable),
            ["--plugin", Path.GetFullPath(manifestPath)], Path.GetDirectoryName(Path.GetFullPath(hostExecutable))!, TimeSpan.FromSeconds(5),
            StandardInput: JsonSerializer.Serialize(request, JsonStore.Options).Replace("\r", "").Replace("\n", "")), cancellationToken);
        if (!response.Success) throw new StudioXException(response.TimedOut ? "PLUGIN_TIMEOUT" : "PLUGIN_EXIT",
            $"插件宿主退出（{response.ExitCode}）：{response.StandardError}");
        DecoderResponse decoded;
        try { decoded = JsonSerializer.Deserialize<DecoderResponse>(response.StandardOutput, JsonStore.Options) ?? throw new JsonException("Empty response."); }
        catch (JsonException ex) { throw new StudioXException("PLUGIN_PROTOCOL", "插件响应不是有效协议数据。" + response.StandardError, ex); }
        if (decoded.ProtocolVersion != 1 || decoded.RequestId != request.RequestId) throw new StudioXException("PLUGIN_PROTOCOL", "插件响应版本或请求 ID 不匹配。");
        if (decoded.ErrorCode is not null || decoded.Result is null) throw new StudioXException(decoded.ErrorCode ?? "PLUGIN_RESULT", decoded.Error ?? "插件未返回结果。");
        if (decoded.Result.Signals is null || decoded.Result.Signals.Count > 1024 || decoded.Result.Signals.Any(s => s is null || !double.IsFinite(s.Value)))
            throw new StudioXException("PLUGIN_RESULT", "插件返回了无效数值或过多信号。");
        return decoded.Result;
    }
}
