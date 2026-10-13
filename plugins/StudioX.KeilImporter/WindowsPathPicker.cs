namespace StudioX.KeilImporter;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static WindowsPathPickerInterop;

/// <summary>在插件自己的 STA 线程打开系统选择窗口；用户取消不改变任何工程或字段。</summary>
public sealed class WindowsPathPicker : IPathPicker
{
    public Task<string?> PickAsync(PathPickerRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("文件选择窗口需要 Windows。"); }
        token.ThrowIfCancellationRequested();
        // 独立宿主不继承 SystemDrive；Shell 会展开此变量来定位缓存，否则会在插件目录生成字面量目录并破坏包校验。
        // 仅补齐当前插件进程的系统盘变量，不修改用户/系统环境或 PATH。
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SystemDrive")))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var drive = Path.GetPathRoot(windows)?.TrimEnd(Path.DirectorySeparatorChar);
            if (drive is not { Length: 2 } || drive[1] != ':') { throw new InvalidOperationException("无法确定 Windows 系统盘，不能打开路径选择窗口。"); }
            Environment.SetEnvironmentVariable("SystemDrive", drive, EnvironmentVariableTarget.Process);
        }
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("文件选择窗口需要 Windows。"); }
                completion.TrySetResult(Show(request, token));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "StudioX Keil path picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static string? Show(PathPickerRequest request, CancellationToken token)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        IFileDialog? dialog = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(CoCreateInstance(OpenDialogClass, 0, 1, DialogInterface, out dialog));
            dialog.GetOptions(out var options);
            // 单选且只接受真实文件系统对象，不改变当前目录，不把工程位置加入系统最近文件。
            dialog.SetOptions((options & ~0x200U) | 0x40U | 0x800U | 0x1000U | 0x8U | 0x02000000U | (request.Folder ? 0x20U : 0));
            dialog.SetTitle(request.Title);
            dialog.SetOkButtonLabel(request.Folder ? "选择文件夹" : "选择文件");
            if (!request.Folder)
            {
                dialog.SetFileTypes(1, [new() { Name = request.FilterLabel, Pattern = request.FilterPattern }]);
                dialog.SetFileTypeIndex(1);
            }
            var initial = Directory.Exists(request.InitialPath) ? request.InitialPath : Path.GetDirectoryName(request.InitialPath);
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            {
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(initial, 0, ShellItemInterface, out var folder));
                try { dialog.SetFolder(folder); }
                finally { Marshal.FinalReleaseComObject(folder); }
            }
            if (!request.Folder && File.Exists(request.InitialPath)) { dialog.SetFileName(Path.GetFileName(request.InitialPath)); }
            Exception? cancellationError = null;
            TimerCallback cancelOnSta = (_, _, _, _) =>
            {
                if (!token.IsCancellationRequested) { return; }
                try
                {
                    ((IOleWindow)dialog).GetWindow(out var window);
                    if (window != 0 && !PostMessageW(window, 0x10, 0, 0)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                }
                catch (Exception error)
                {
                    if (cancellationError is null) { Trace.WriteLine(error); }
                    cancellationError ??= error;
                }
            };
            // 在窗口自身的 STA 消息循环中请求关闭，取消回调不跨线程访问 COM；异常不能越过原生回调边界。
            var timer = token.CanBeCanceled ? SetTimer(0, 0, 100, cancelOnSta) : 0;
            if (token.CanBeCanceled && timer == 0) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                token.ThrowIfCancellationRequested();
                var result = dialog.Show(FindOwner());
                if (cancellationError is { } error) { throw new InvalidOperationException("关闭路径选择窗口失败。", error); }
                token.ThrowIfCancellationRequested();
                if (result == Cancelled) { return null; }
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out var item);
                try
                {
                    item.GetDisplayName(0x80058000U, out var path);
                    try { return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException("选择窗口没有返回文件系统路径。"); }
                    finally { Marshal.FreeCoTaskMem(path); }
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            finally
            {
                if (timer != 0 && !KillTimer(0, timer)) { Trace.WriteLine("路径选择窗口的取消定时器已不可用。"); }
                GC.KeepAlive(cancelOnSta);
            }
        }
        finally
        {
            if (dialog is not null) { Marshal.FinalReleaseComObject(dialog); }
            CoUninitialize();
        }
    }

    [SupportedOSPlatform("windows")]
    private static nint FindOwner()
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "MCU StudioX.exe"));
        var foreground = GetForegroundWindow();
        nint owner = 0;
        var candidates = 0;
        foreach (var process in Process.GetProcessesByName("MCU StudioX"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.Equals(executable, StringComparison.OrdinalIgnoreCase) == true && process.MainWindowHandle != 0)
                    {
                        if (process.MainWindowHandle == foreground) { return foreground; }
                        owner = process.MainWindowHandle;
                        candidates++;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // IDE 可能正好退出；无有效所有者时选择窗口由插件进程持有。
                    Trace.WriteLine(error);
                }
            }
        }
        // 多实例且没有前台匹配时，不让选择窗口意外阻塞另一份 IDE。
        return candidates == 1 ? owner : 0;
    }

}
