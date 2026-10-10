namespace StudioX.Application.CodeIntelligence;

using System.Text;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private readonly SemaphoreSlim analysisLogGate = new(1, 1);
    private readonly string analysisLogName = $"language-analysis/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log";
    private const long AnalysisLogLimit = 2 * 1024 * 1024;
    private const int AnalysisLogArchives = 3;
    public string AnalysisLogPath => PathBoundary.Resolve(dataDirectory, analysisLogName);

    /// <summary>后台分析异常与工具原文独立保存，不作为用户构建输出。</summary>
    public void RecordAnalysisLog(string message) => log.Enqueue($"[{DateTimeOffset.Now:O}] {message}");

    public async Task FlushAnalysisLogAsync(CancellationToken token = default)
    {
        await analysisLogGate.WaitAsync(token).ConfigureAwait(false);
        var pending = new List<string>();
        try
        {
            // 写入成功前保留整个批次；磁盘不可写时允许重试，不静默丢弃原始诊断。
            while (log.TryDequeue(out var line))
            {
                pending.Add(line);
            }
            if (pending.Count == 0)
            {
                return;
            }
            var path = AnalysisLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length >= AnalysisLogLimit)
            {
                for (var index = AnalysisLogArchives; index > 0; index--)
                {
                    var source = index == 1 ? path : PathBoundary.Resolve(dataDirectory, analysisLogName + "." + (index - 1));
                    var destination = PathBoundary.Resolve(dataDirectory, analysisLogName + "." + index);
                    if (File.Exists(source))
                    {
                        File.Move(source, destination, overwrite: true);
                    }
                }
            }
            await File.AppendAllTextAsync(path, string.Join('\n', pending) + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
        }
        catch
        {
            foreach (var line in pending)
            {
                log.Enqueue(line);
            }
            throw;
        }
        finally { analysisLogGate.Release(); }
    }

    /// <summary>按需读取最近内容；完整原文保存在当前会话日志及三个轮转文件中。</summary>
    public async Task<string> ReadAnalysisLogAsync(CancellationToken token = default)
    {
        await FlushAnalysisLogAsync(token).ConfigureAwait(false);
        await analysisLogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            const int viewLimit = 256 * 1024;
            var text = "";
            for (var index = AnalysisLogArchives; index >= 0; index--)
            {
                var path = PathBoundary.Resolve(dataDirectory, analysisLogName + (index == 0 ? "" : "." + index));
                if (File.Exists(path))
                {
                    text += await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                    if (text.Length > viewLimit)
                    {
                        text = text[^viewLimit..];
                    }
                }
            }
            return "[独立语言分析日志；视图最多显示最近 256 Ki 字符，每次约 2 MiB 轮转，保留三个旧文件]\n" +
                (text.Length == 0 ? "暂无语言分析日志。" : text);
        }
        finally { analysisLogGate.Release(); }
    }
}
