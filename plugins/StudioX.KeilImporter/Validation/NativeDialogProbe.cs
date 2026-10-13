namespace StudioX.KeilImporter.Validation;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>仅操作本验收进程中标题精确匹配的自有选择窗口，不查找或触碰用户 IDE 窗口。</summary>
internal static class NativeDialogProbe
{
    internal static async Task PressOwnDialogAsync(string title, nuint command, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            nint matching = 0;
            EnumCallback find = (window, _) =>
            {
                GetWindowThreadProcessId(window, out var process);
                if (process != Environment.ProcessId) { return true; }
                var text = new StringBuilder(256);
                GetWindowTextW(window, text, text.Capacity);
                if (text.ToString() == title) { matching = window; return false; }
                return true;
            };
            EnumWindows(find, 0);
            GC.KeepAlive(find);
            if (matching != 0)
            {
                await Task.Delay(400, token);
                if (!PostMessageW(matching, 0x111, command, 0)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                return;
            }
            await Task.Delay(50, token);
        }
    }

    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int characters);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
