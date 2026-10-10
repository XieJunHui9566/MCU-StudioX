namespace StudioX.SecurityValidation;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StudioX.Foundation;

internal static class DirectoryMoveChecks
{
    public static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows sharing checks require Windows.");
        }
        var source = Path.Combine(root, "move-sharing");
        var destination = source + "-published";
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "content.txt");
        await File.WriteAllTextAsync(file, "verified content");
        var held = HoldDirectory(source);
        var move = DirectoryMoves.MoveAsync(source, destination);
        try
        {
            await Task.Delay(250);
            Require(!move.IsCompleted && Directory.Exists(source) && !Directory.Exists(destination),
                $"sharing conflict must retain unpublished source; status={move.Status}; error={move.Exception}");
        }
        finally
        {
            held.Dispose();
        }
        await move;
        Require(await File.ReadAllTextAsync(Path.Combine(destination, "content.txt")) == "verified content",
            "released handle must allow atomic publication without changing content");

        source = Path.Combine(root, "move-persistent");
        destination = source + "-published";
        Directory.CreateDirectory(source);
        file = Path.Combine(source, "content.txt");
        await File.WriteAllTextAsync(file, "retain me");
        using (var persistent = HoldDirectory(source))
        {
            try
            {
                await DirectoryMoves.MoveAsync(source, destination).WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("persistent sharing conflict was ignored");
            }
            catch (IOException error) when ((uint)error.HResult is 0x80070020 or 0x80070021) { }
            Require(Directory.Exists(source) && !Directory.Exists(destination), "failed move must preserve source");
            using var cancelled = new CancellationTokenSource();
            move = DirectoryMoves.MoveAsync(source, destination, cancelled.Token);
            cancelled.Cancel();
            try
            {
                await move;
                throw new InvalidOperationException("cancelled sharing retry completed");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            Require(Directory.Exists(source) && !Directory.Exists(destination), "cancellation must preserve source");
        }
        Require(await File.ReadAllTextAsync(file) == "retain me", "failed and cancelled moves must retain content");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static SafeFileHandle HoldDirectory(string path)
    {
        // 子文件句柄不能稳定阻止父目录重命名；直接持有不共享删除的目录句柄。
        var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint attributes, IntPtr template);
}
