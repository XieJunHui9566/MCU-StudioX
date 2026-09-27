namespace StudioX.Engine.Debugging;

/// <summary>传输边界：UI 不执行 GDB 文本，不持有进程。离线传输与未来进程传输共用 MI 解析和命令映射。</summary>
public interface IGdbMiTransport : IAsyncDisposable
{
    event Action<string>? RecordReceived;
    Task<string> ExecuteAsync(string command, CancellationToken token = default);
}
