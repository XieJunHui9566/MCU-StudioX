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
                ((uint)error.HResult is 0x80070020 or 0x80070021 ||
                 (uint)error.HResult == 0x80070005 && Directory.Exists(source) && !Directory.Exists(destination)))
            {
                // Windows 对被占用目录也可能返回 ACCESS_DENIED；仅在移动尚未发生时有界重试。
                // 不修改权限、不替换目标；持续拒绝仍抛出原始异常，取消也不能发布部分目录。
                await Task.Delay(Math.Min(100 << attempt, 1000), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
