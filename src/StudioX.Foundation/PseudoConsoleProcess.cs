namespace StudioX.Foundation;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>Windows ConPTY 会话。输出始终在独立线程排空，关闭伪控制台时不会与输出管道互相等待。</summary>
public sealed class PseudoConsoleProcess : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private FileStream? input, output;
    private Process? process;
    private nint console, job;
    private Task reader = Task.CompletedTask;
    private Task completion = Task.CompletedTask;
    private int? exitCode;
    private Task? disposal;
    public Task Completion => completion;
    public int? ExitCode => exitCode;
    public event Action<string>? Output;
    public event Action<Exception>? ReadError;

    public void Start(string executable, string arguments, string directory, IReadOnlyDictionary<string, string> environment, int columns, int rows)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("工程终端需要 Windows 10 1809 或更新版本。");
        if (process is not null || console != 0) throw new InvalidOperationException("Terminal already started.");
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint attributes = 0, environmentBlock = 0;
        PROCESS_INFORMATION info = default;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) || !CreatePipe(out outputRead, out outputWrite, 0, 0)) Fail();
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new((short)columns, (short)rows), inputRead!, outputWrite!, 0, out console));
            nuint bytes = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) Fail();
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020016, console, (nuint)IntPtr.Size, 0, 0)) Fail();
            // 即使 IDE 由重定向输出的父进程启动，也强制让三个标准句柄连接 ConPTY。
            // STARTF_USESTDHANDLES + NULL 避免继承父进程的文件/管道句柄。
            var startup = new STARTUPINFOEX { StartupInfo = new() { cb = Marshal.SizeOf<STARTUPINFOEX>(), Flags = 0x100 }, AttributeList = attributes };
            environmentBlock = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0");
            job = CreateJobObject(0, null);
            if (job == 0) Fail();
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) Fail();
            // 先以暂停状态创建并放入 Job，避免退出 IDE 后遗留 git / ssh / 子 Shell。
            if (!CreateProcess(executable, new StringBuilder('"' + executable + "\" " + arguments), 0, 0, false,
                0x00080000 | 0x00000400 | 0x00000004, environmentBlock, directory, ref startup, out info)) Fail();
            if (!AssignProcessToJobObject(job, info.Process)) Fail();
            process = Process.GetProcessById((int)info.ProcessId);
            input = new FileStream(inputWrite!, FileAccess.Write, 4096, false); inputWrite = null;
            output = new FileStream(outputRead!, FileAccess.Read, 4096, false); outputRead = null;
            reader = Task.Run(ReadLoop);
            if (ResumeThread(info.Thread) == uint.MaxValue) Fail();
            completion = WaitForExitAsync(process);
        }
        catch
        {
            if (info.Process != 0) TerminateProcess(info.Process, 1);
            if (job != 0) { CloseHandle(job); job = 0; }
            if (console != 0) { ClosePseudoConsole(console); console = 0; }
            input?.Dispose(); output?.Dispose(); process?.Dispose(); process = null;
            throw;
        }
        finally
        {
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            if (attributes != 0) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (environmentBlock != 0) Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private async Task WaitForExitAsync(Process source) { await source.WaitForExitAsync(); exitCode = source.ExitCode; }
    private void ReadLoop()
    {
        try
        {
            using var text = new StreamReader(output!, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var buffer = new char[4096];
            int count;
            while ((count = text.Read(buffer, 0, buffer.Length)) > 0) Output?.Invoke(new string(buffer, 0, count));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { lock (sync) { if (disposal is null) ReadError?.Invoke(ex); } }
    }
    public async Task WriteAsync(string text)
    {
        await inputGate.WaitAsync();
        try
        {
            var stream = input ?? throw new InvalidOperationException("终端未运行。");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text)); await stream.FlushAsync();
        }
        finally { inputGate.Release(); }
    }
    public void Resize(int columns, int rows)
    {
        lock (sync) if (console != 0 && disposal is null)
            Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, new((short)columns, (short)rows)));
    }
    public ValueTask DisposeAsync()
    {
        lock (sync) return new(disposal ??= Task.Run(async () =>
        {
            if (job != 0) { CloseHandle(job); job = 0; }
            // reader 必须继续排空到 ClosePseudoConsole 返回，禁止先取消 reader。
            if (console != 0) { ClosePseudoConsole(console); console = 0; }
            await inputGate.WaitAsync();
            try { input?.Dispose(); input = null; } finally { inputGate.Release(); }
            await reader;
            await completion;
            output?.Dispose(); output = null;
            process?.Dispose(); process = null;
        }));
    }
    private static void Fail() => throw new Win32Exception(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)] private readonly record struct COORD(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO
    {
        public int cb; public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(nint console, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
