namespace StudioX.Engine.Pic;

/// <summary>同步保存收到的原始输出，使取消、失败和界面回调异常都保留诊断。</summary>
internal sealed class PicDownloadLog(string path, IProgress<string>? output) : IProgress<string>, IDisposable
{
    private readonly StreamWriter writer = new(path) { AutoFlush = true };
    private readonly object sync = new();
    public void Record(string value)
    {
        lock (sync)
        {
            writer.Write(value);
        }
    }
    public void Report(string value)
    {
        Record(value);
        output?.Report(value);
    }
    public void Dispose()
    {
        lock (sync)
        {
            writer.Dispose();
        }
    }
}
