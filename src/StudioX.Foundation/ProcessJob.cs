namespace StudioX.Foundation;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>为明确请求的 Windows 构建建立内核进程组；不能仅等待根进程退出就清理固件。</summary>
internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal ProcessJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Contained tool processes currently require Windows.");
        }
        handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
    }
    internal void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw new Win32Exception(error);
        }
    }
    internal async Task StopAsync()
    {
        var processes = new List<SafeProcessHandle>();
        try
        {
            CaptureProcessHandles(processes);
            if (!TerminateJobObject(handle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // TerminateJobObject 是异步终止。ActiveProcesses 归零时，子进程的文件清理仍可能未结束。
            // 保留终止前的内核句柄并等其信号，不能只看根进程或组内计数后立即重新占用输出锁。
            CaptureProcessHandles(processes);
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var exited = true;
                foreach (var process in processes)
                {
                    var state = WaitForSingleObject(process, 0);
                    if (state == uint.MaxValue)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    exited &= state == 0;
                }
                if (accounting.ActiveProcesses == 0 && exited)
                {
                    return;
                }
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("Tool process group did not exit; output must remain invalid.");
                }
                await Task.Delay(20);
            }
        }
        finally { foreach (var process in processes) { process.Dispose(); } }
    }
    private void CaptureProcessHandles(List<SafeProcessHandle> processes)
    {
        var capacity = 32;
        while (true)
        {
            var length = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!QueryProcessIds(handle, 3, buffer, (uint)length, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 234 && capacity < 65536)
                    {
                        capacity *= 2;
                        continue;
                    }
                    throw new Win32Exception(error);
                }
                var count = Marshal.ReadInt32(buffer, 4);
                for (var i = 0; i < count; i++)
                {
                    var id = checked((uint)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64());
                    var process = OpenProcess(0x100000 | 0x1000, false, id);
                    if (process.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        process.Dispose();
                        if (error == 87)
                        {
                            continue;
                        } // 已退出且 PID 已消失；不能用 PID 查找另一个进程代替。
                        throw new Win32Exception(error);
                    }
                    if (!IsProcessInJob(process, handle, out var member))
                    {
                        var error = Marshal.GetLastWin32Error();
                        process.Dispose();
                        throw new Win32Exception(error);
                    }
                    if (member)
                    {
                        processes.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, uint length, nint returnLength);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] private static extern bool QueryProcessIds(SafeFileHandle job, int infoClass, nint info, uint length, nint returnLength);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool member);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
