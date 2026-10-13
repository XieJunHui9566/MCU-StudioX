namespace StudioX.Foundation;

/// <summary>保留目录原子移动语义，仅对 Windows 短暂共享冲突做有界重试。</summary>
public static class DirectoryMoves
{
    public static async Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 &&
                (uint)error.HResult is 0x80070020 or 0x80070021)
            {
                // 扫描器或刚退出的宿主可能短暂持有文件；不重试权限、路径及内容错误。
                await Task.Delay(Math.Min(100 << attempt, 1000), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
