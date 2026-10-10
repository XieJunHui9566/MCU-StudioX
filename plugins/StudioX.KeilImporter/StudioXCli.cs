namespace StudioX.KeilImporter;

using System.Diagnostics;
using System.Text;

/// <summary>只调用已安装产品的公开 CLI；参数逐项传递，不经过 shell。</summary>
public static class StudioXCli
{
    public static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var result = await ExecuteAsync(executable, arguments, token);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("StudioX CLI 返回 " + result.ExitCode + ":\n" + result.StandardError + "\n" + result.StandardOutput);
        }
        return result.StandardOutput;
    }

    public static async Task<CliOutput> ExecuteAsync(string executable, string[] arguments, CancellationToken token, Action<string, string>? output = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(arguments.FirstOrDefault() == "build" ? 12 : 3));
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动已安装 StudioX CLI。");
        }
        try
        {
            var stdout = ReadLimitedAsync(process.StandardOutput, deadline, "stdout", output);
            var stderr = ReadLimitedAsync(process.StandardError, deadline, "stderr", output);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationTokenSource deadline, string channel, Action<string, string>? output)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0)
            {
                output?.Invoke(channel, new string(buffer, 0, count));
                if (text.Length + count > 1024 * 1024)
                {
                    throw new InvalidOperationException("CLI 输出超过 1 MiB，操作中止。");
                }
                text.Append(buffer, 0, count);
            }
        }
        catch
        {
            // 任一输出通道失败都立即取消等待，避免进程阻塞到整个编译时限。
            deadline.Cancel();
            throw;
        }
        return text.ToString();
    }
}
