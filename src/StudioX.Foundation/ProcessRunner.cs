namespace StudioX.Foundation;

using System.Diagnostics;
using System.Text;

/// <summary>参数数组传递，不经过 Shell；取消和超时清理整个工具进程树。</summary>
public sealed class ProcessRunner
{
    private const int MaximumLogCharacters = 2 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(request.Executable) || !File.Exists(request.Executable))
            throw new StudioXException("TOOL_MISSING", $"工具组件不存在：{request.Executable}");
        var start = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        if (request.RemoveEnvironment is not null)
            foreach (var key in request.RemoveEnvironment) start.Environment.Remove(key);
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new StudioXException("TOOL_START", "无法启动工具进程。");
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var output = DrainAsync(process.StandardOutput, linked.Token, request.Output);
        var error = DrainAsync(process.StandardError, linked.Token, request.Output);
        var input = request.StandardInput is not null ? WriteInputAsync(process, request.StandardInput, linked.Token) : Task.CompletedTask;
        var timedOut = false;
        try { await Task.WhenAll(process.WaitForExitAsync(linked.Token), output, error, input); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 退出与取消可能同时发生。 */ }
            await process.WaitForExitAsync(CancellationToken.None);
            timedOut = !cancellationToken.IsCancellationRequested;
        }
        var stdout = await output;
        var stderr = await error;
        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(process.ExitCode, stdout.Text, stderr.Text, timedOut, stdout.Truncated || stderr.Truncated);
    }

    private static async Task WriteInputAsync(Process process, string input, CancellationToken token)
    {
        try { await process.StandardInput.WriteLineAsync(input.AsMemory(), token); }
        catch (IOException) { /* 提前退出的宿主由退出码和原始诊断报告。 */ }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken token, IProgress<string>? progress)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                var retained = Math.Min(count, MaximumLogCharacters - text.Length);
                text.Append(buffer, 0, retained);
                if (retained > 0) progress?.Report(new string(buffer, 0, retained));
                truncated |= retained != count;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return (text.ToString(), truncated);
    }
}
