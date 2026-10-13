namespace StudioX.KeilImporter;

using System.Runtime.InteropServices;

/// <summary>Windows Common Item Dialog 的固定 COM ABI；不引用 WPF 或 IDE 私有程序集。</summary>
internal static class WindowsPathPickerInterop
{
    internal const int Cancelled = unchecked((int)0x800704C7);
    internal static readonly Guid OpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    internal static readonly Guid DialogInterface = new("42F85136-DB7E-439C-85F1-E4075D135FC8");
    internal static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(nint reserved, uint options);
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IFileDialog dialog);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(string path, nint context, in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetForegroundWindow();
    internal delegate void TimerCallback(nint window, uint message, nuint timer, uint time);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, TimerCallback callback);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern;
    }

    // COM vtable 必须按 SDK 中 IModalWindow / IFileDialog 的顺序完整声明，不能省略未调用的方法。
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleWindow
    {
        void GetWindow(out nint window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }

}
