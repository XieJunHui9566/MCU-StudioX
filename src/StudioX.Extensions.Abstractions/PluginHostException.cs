namespace StudioX.Extensions.Abstractions;

/// <summary>宿主工具返回的稳定错误；插件可按 Code 处理，或重新抛出以保留原错误码。</summary>
/// <remarks>Message 保留宿主的原始错误诊断。调用者取消仍使用 OperationCanceledException，不转换为此类型。</remarks>
public sealed class PluginHostException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>与宿主协议 errorCode 一致的稳定标识，不依赖可翻译的消息文本。</summary>
    public string Code { get; } = code;
}
