using StudioX.Foundation;

internal static class DirectoryMoveChecks
{
    public static async Task RunAsync(string root, Action<string> passed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var source = Path.Combine(root, "move-held");
        var target = Path.Combine(root, "move-released");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "preserve bytes");
        using (var held = Hold(source))
        {
            try
            {
                Directory.Move(source, target);
                throw new InvalidOperationException("The fixture did not hold a real directory lease.");
            }
            catch (IOException error) when ((uint)error.HResult is 0x80070005 or 0x80070020 or 0x80070021)
            {
                passed("real Windows directory sharing error reproduced: 0x" + ((uint)error.HResult).ToString("X8"));
            }
            var moving = DirectoryMoves.MoveAsync(source, target);
            await Task.Delay(30);
            if (moving.IsCompleted || !Directory.Exists(source) || Directory.Exists(target))
            {
                throw new InvalidOperationException("Held directory must remain unpublished until its lease releases.");
            }
            held.Dispose();
            await moving;
        }
        if (Directory.Exists(source) || await File.ReadAllTextAsync(Path.Combine(target, "content.txt")) != "preserve bytes")
        {
            throw new InvalidOperationException("Atomic directory move did not retain exact content.");
        }
        passed("held Windows directory is retried atomically after its sharing lease releases");

        var cancelledSource = Path.Combine(root, "move-cancelled");
        var cancelledTarget = Path.Combine(root, "move-cancelled-target");
        Directory.CreateDirectory(cancelledSource);
        using (var held = Hold(cancelledSource))
        {
            using (var cancellation = new CancellationTokenSource(30))
            {
                try
                {
                    await DirectoryMoves.MoveAsync(cancelledSource, cancelledTarget, cancellation.Token);
                    throw new InvalidOperationException("Cancelled sharing wait unexpectedly completed.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
        if (!Directory.Exists(cancelledSource) || Directory.Exists(cancelledTarget))
        {
            throw new InvalidOperationException("Cancellation must not publish a partial directory.");
        }
        passed("cancellation interrupts sharing retries without publishing the target");

        var occupied = Path.Combine(root, "move-existing");
        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "content.txt"), "existing target");
        try
        {
            await DirectoryMoves.MoveAsync(cancelledSource, occupied);
            throw new InvalidOperationException("Existing destination was replaced.");
        }
        catch (IOException) { }
        if (!Directory.Exists(cancelledSource) || await File.ReadAllTextAsync(Path.Combine(occupied, "content.txt")) != "existing target")
        {
            throw new InvalidOperationException("Existing destination changed.");
        }
        passed("existing destination and original content remain protected");
    }

    private static FileStream Hold(string path)
    {
        // 子文件缺少共享删除许可时，Windows 的父目录重命名会报告 ACCESS_DENIED。
        var file = Path.Combine(path, "content.txt");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, "held fixture");
        }
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
