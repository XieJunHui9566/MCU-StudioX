namespace StudioX.Engine.Debugging;

/// <summary>调试交互契约；各适配器负责真实工具协议和能力边界，通知沿用既有事件封装。</summary>
public interface IDebugAdapter : IAsyncDisposable
{
    event Action<string>? RecordReceived;
    event Action<string>? Trace;
    Task<MiValue> SendAsync(string command, CancellationToken token = default);
    Task<(string Number, bool Verified, SourceLocation? Location)> InsertAsync(SourceBreakpoint point, CancellationToken token);
    Task<string> EvaluateAsync(string expression, CancellationToken token = default);
    Task<MiValue[]> BreakpointInfoAsync(CancellationToken token = default);
    Task<MiValue> DeleteAsync(string id, CancellationToken token);
    Task<MiValue> EnableAsync(string id, bool enabled, CancellationToken token);
    Task<MiValue> ActionAsync(DebugAction action, CancellationToken token);
    Task<DebugSnapshot> ReadAsync(IReadOnlyList<string> watches, int frame, CancellationToken token);
    Task<string> ReadMemoryAsync(uint address, int count, CancellationToken token);
    Task<DebugInstruction[]> ReadDisassemblyAsync(uint address, int byteCount, CancellationToken token = default);
}
