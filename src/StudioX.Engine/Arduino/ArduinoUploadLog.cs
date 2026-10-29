namespace StudioX.Engine.Arduino;

/// <summary>工具输出先同步落盘再通知界面，取消与异常也保留已收到的诊断。</summary>
internal sealed class ArduinoUploadLog(string path, IProgress<string>? observer) : IProgress<string>, IDisposable
{
    private readonly StreamWriter writer = new(path) { AutoFlush = true };
    private readonly object sync = new();

    public void Report(string value)
    {
        lock (sync)
        {
            writer.Write(value);
        }
        observer?.Report(value);
    }

    public void Dispose()
    {
        lock (sync)
        {
            writer.Dispose();
        }
    }
}
