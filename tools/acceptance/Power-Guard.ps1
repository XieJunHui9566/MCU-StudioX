function Start-AcceptancePowerRequest
{
    # 只在验收进程存活期间阻止自动休眠，不更改用户电源计划或注册表。
    if (!('StudioXAcceptance.PowerRequest' -as [type]))
    {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace StudioXAcceptance {
    public static class PowerRequest {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Reason {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReason;
            private IntPtr Padding1;
            private IntPtr Padding2;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr PowerCreateRequest(ref Reason reason);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PowerSetRequest(IntPtr handle, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PowerClearRequest(IntPtr handle, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
'@
    }
    $reason = New-Object StudioXAcceptance.PowerRequest+Reason
    $reason.Version = 0
    $reason.Flags = 1
    $reason.SimpleReason = 'MCU StudioX offline acceptance is running.'
    $handle = [StudioXAcceptance.PowerRequest]::PowerCreateRequest([ref]$reason)
    if ($handle -eq [IntPtr]::Zero -or $handle -eq [IntPtr]::new(-1))
    {
        throw 'Unable to create the temporary acceptance power request.'
    }
    if (![StudioXAcceptance.PowerRequest]::PowerSetRequest($handle, 0) -or
        ![StudioXAcceptance.PowerRequest]::PowerSetRequest($handle, 3))
    {
        Stop-AcceptancePowerRequest $handle
        throw 'Unable to prevent automatic sleep during acceptance.'
    }
    return $handle
}

function Stop-AcceptancePowerRequest([IntPtr]$Handle)
{
    if ($Handle -eq [IntPtr]::Zero)
    {
        return
    }
    [StudioXAcceptance.PowerRequest]::PowerClearRequest($Handle, 3) | Out-Null
    [StudioXAcceptance.PowerRequest]::PowerClearRequest($Handle, 0) | Out-Null
    [StudioXAcceptance.PowerRequest]::CloseHandle($Handle) | Out-Null
}
