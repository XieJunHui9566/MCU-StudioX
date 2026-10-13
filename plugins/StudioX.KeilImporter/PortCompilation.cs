namespace StudioX.KeilImporter;

using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

/// <summary>发布副本后使用现有受管理工具编译；失败保留副本和原始诊断，便于用户继续适配。</summary>
public static class PortCompilation
{
    public const string LogPath = ".studiox/keil-build.log";

    public static async Task<(bool Verified, string? Error)> VerifyAsync(string cli, string project, CancellationToken token,
        IReadOnlyDictionary<string, string>? sourceHashes = null)
    {
        var runtime = Path.GetDirectoryName(Path.GetDirectoryName(cli))!;
        var toolsets = Path.Combine(runtime, "toolsets");
        var verified = false;
        int? exitCode = null;
        string? error = null;
        var log = new StringBuilder();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputGate = new object();
        try
        {
            if (!Directory.Exists(toolsets))
            {
                throw new InvalidOperationException("当前 IDE 缺少 runtime/toolsets，请通过 IDE 安装工程所锁定的开发环境后重新编译。");
            }
            var output = await StudioXCli.ExecuteAsync(cli, ["build", project, toolsets], token, (channel, text) =>
            {
                lock (outputGate)
                {
                    (channel == "stdout" ? stdout : stderr).Append(text);
                }
            });
            exitCode = output.ExitCode;
            var artifacts = Path.Combine(project, ".build");
            verified = exitCode == 0 && Directory.Exists(artifacts) &&
                Directory.EnumerateFiles(artifacts, "firmware.elf", SearchOption.AllDirectories).Any(file => new FileInfo(file).Length > 0);
            if (!verified)
            {
                error = "自动编译未通过，请查看完整编译日志并在 IDE 中继续适配。CLI 退出码：" + exitCode;
            }
        }
        catch (OperationCanceledException exception)
        {
            error = "自动编译已取消或超时，移植副本已保留，请在 IDE 中重新编译。";
            log.AppendLine().Append(exception);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            log.AppendLine().Append(exception);
        }
        finally
        {
            // 即使用户取消也保留编译状态，避免把已经发布的副本误报为已验证。
            // 分开保留两路字节顺序，避免并发读取把一条编译诊断的文件位置与错误正文拼断。
            var text = stdout.ToString() + "\n" + stderr.ToString() + log + (error is null ? "" : "\n" + error);
            var encoding = new UTF8Encoding(false);
            await File.WriteAllTextAsync(Path.Combine(project, LogPath), text, encoding, CancellationToken.None);
            var logSha256 = Convert.ToHexString(SHA256.HashData(encoding.GetBytes(text)));
            await File.WriteAllTextAsync(Path.Combine(project, ".studiox", "keil-build.json"),
                JsonSerializer.Serialize(new
                {
                    formatVersion = 1,
                    compilationVerified = verified,
                    exitCode,
                    error,
                    log = LogPath,
                    logSha256,
                    sourceHashes = sourceHashes ?? new Dictionary<string, string>()
                }, DeviceCatalog.Json), CancellationToken.None);
        }
        return (verified, error);
    }
}
