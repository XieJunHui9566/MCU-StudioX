namespace StudioX.Application;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StudioX.Foundation;

/// <summary>在专用 STA 中调用 Windows Shell 回收，不通过永久递归删除降级。</summary>
internal static class WindowsRecycleBin
{
    public static Task RecycleDirectoryAsync(string directory, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("工程回收仅支持 Windows。");
        }
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                {
                    Recycle(directory);
                }
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "StudioX project recycle"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Shell 开始后由其完成回收或用户取消；不让调用者提前离开并误报结果。
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static void Recycle(string directory)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
        IShellItem? item = null;
        try
        {
            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, 0, ref shellItemId, out item));
            // RECYCLEONDELETE/ADDUNDORECORD 使用系统回收与撤销。无法回收时保留 Shell 的永久删除警告。
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x4000 | 0x0010);
            operation.DeleteItem(item, 0);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted)
            {
                throw new OperationCanceledException("Windows 已取消工程删除。");
            }
            if (Directory.Exists(directory))
            {
                throw new StudioXException("PROJECT_DELETE_INCOMPLETE", "Windows 未完成工程回收；最近记录已保留，请检查工程文件夹。");
            }
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
            Marshal.ReleaseComObject(operation);
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    // 顺序必须与 shobjidl_core.h 的 COM vtable 一致；未使用的参数保留原生指针槽位。
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, nint name, nint sink);
        void RenameItems(nint items, nint name);
        void MoveItem(nint item, nint destination, nint name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, nint name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(IShellItem item, nint sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
