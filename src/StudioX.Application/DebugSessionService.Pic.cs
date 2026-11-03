namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Engine.Pic;

public sealed partial class DebugSessionService
{
    public bool IsPicDebug => adapter is MdbDebugAdapter;

    public async Task StartPicAsync(PicDebugService service, string? serial, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        IDisposable? ownership = null;
        try
        {
            if (IsActive || ProjectDirectory is null)
            {
                throw Error("请打开 PIC CMake 工程并结束已有会话。");
            }
            var project = await ProjectService.ReadAsync(ProjectDirectory, token);
            if (project.Kind != ProjectKind.Pic)
            {
                throw Error("当前工程不是 PIC 工程。");
            }
            _ = PicDebugTarget.Require(project.DeviceId);
            if (serial is not null)
            {
                if (devices is null)
                {
                    throw Error("PICkit3 调试需要设备所有权服务。");
                }
                ownership = await devices.ReserveAsync("usb:pickit3:" + serial, token);
            }
            SetState(DebugState.Starting, "CMake / XC8 调试映像已准备；回读备份并启动 MDB…");
            var current = await service.StartAsync(ProjectDirectory, serial, new PicProgress(Trace), token);
            var finish = current.FinishSession;
            var heldOwnership = ownership;
            ownership = null;
            current.FinishSession = async () =>
            {
                try
                {
                    if (finish is not null)
                    {
                        await finish();
                    }
                }
                finally { heldOwnership?.Dispose(); }
            };
            adapter = current;
            IsHardware = serial is not null;
            HardwareTarget = new(project.DeviceId, "PIC16 · 程序字地址", false);
            HardwareTargetName = project.DeviceId + (serial is null ? " / MDB Simulator" : " / PICkit3 " + serial);
            SessionLogPath = service.LogPath;
            bound.Clear();
            pauseRequested = false;
            breakpoints = breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray();
            recordPump ??= ReadRecordsAsync();
            receiver = record => records.Writer.TryWrite((current, record));
            current.RecordReceived += receiver;
            current.Trace += Trace;
            for (var i = 0; i < breakpoints.Length; i++)
            {
                breakpoints[i] = await BindAsync(breakpoints[i], token);
            }
            Snapshot = await current.ReadAsync(watches, 0, token);
            SetState(DebugState.Stopped, serial is null ? "已暂停 · MDB 真实指令模拟，未连接硬件" : "PICkit3 已连接并暂停；退出时恢复原程序并校验");
        }
        catch (Exception failure)
        {
            Trace(failure.ToString());
            await ReleaseAdapterAsync();
            Snapshot = DebugSnapshot.Empty;
            SetState(DebugState.Faulted, "PIC 调试启动失败：" + failure.Message);
            throw;
        }
        finally { ownership?.Dispose(); gate.Release(); }
    }

    public async Task WritePicFileRegistersAsync(uint address, byte[] values, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (adapter is not MdbDebugAdapter pic)
            {
                throw Error("当前会话不是 PIC 调试。");
            }
            await pic.WriteFileRegistersAsync(address, values, token);
            Snapshot = Highlight(await pic.ReadAsync(watches, Snapshot.SelectedFrame, token), Snapshot);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    private sealed class PicProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
