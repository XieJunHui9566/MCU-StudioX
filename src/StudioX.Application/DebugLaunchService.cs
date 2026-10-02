namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>只协调现有调试所有者，阶段结果取自实际工具与一致性校验。</summary>
public sealed class DebugLaunchService(DebugSessionService debugger, OpenOcdService downloads)
{
    private readonly object sync = new();
    private int connecting;
    private DebugLaunchReport current = new(DateTimeOffset.UtcNow, false, []);
    public event Action? Changed;
    public DebugLaunchReport Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public async Task StartAsync(string project, bool connectUnderReset, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0)
        {
            throw new StudioXException("DEBUG_START_BUSY", "连接检查正在进行。");
        }
        try
        {
            lock (sync)
            {
                current = new(DateTimeOffset.UtcNow, true, new[]
                {
                    new DebugLaunchStep("prepare", "本地准备", "进行中", "核对工程、源码与 ELF、工具完整性"),
                    new("probe", "探针连接", "等待", "取得 StudioX 探针独占租约"),
                    new("target", "目标识别", "等待", "核对器件身份与容量"),
                    new("symbols", "调试器启动", "等待", "加载独立 ELF"),
                    new("verify", "固件校验", "等待", "只读校验，未确认匹配前不展示源码快照"),
                    new("snapshot", "暂停快照", "等待", "源码、断点与寄存器"),
                    new("restore", "退出恢复", "等待", "结束时才核对目标恢复运行")
                });
            }
            Changed?.Invoke();
            var preparation = await HardwareDebugPreparer.PrepareAsync(project, downloads, token);
            preparation = preparation with
            {
                ConnectUnderReset = connectUnderReset,
                Progress = new Reporter(this)
            };
            // 配置阶段即拒绝不支持的复位模式，不启动探针后再报参数错误。
            _ = OpenOcdDebugPlanner.Create(project, preparation.Configuration, preparation.Tools, preparation.Elf, connectUnderReset: connectUnderReset);
            Set(new("prepare", "本地源码、ELF、连接模式与工具检查通过。", true));
            lock (sync)
            {
                current = current with
                {
                    LogPath = preparation.LogPath
                };
            }
            await debugger.StartHardwareAsync(preparation, token);
            Set(new("ready", "已连接并暂停，固件一致性已确认。"));
        }
        catch (Exception error)
        {
            lock (sync)
            {
                current = current with
                {
                    Busy = false,
                    Diagnostic = error.ToString(),
                    Steps = current.Steps.Select(s => s.Status == "进行中" ? s with { Status = token.IsCancellationRequested ? "已取消" : "失败" } : s).ToArray()
                };
            }
            Changed?.Invoke();
            throw;
        }
        finally { Interlocked.Exchange(ref connecting, 0); }
    }

    private void Set(DebugStartupProgress value)
    {
        lock (sync)
        {
            var terminal = value.Stage is "ready" or "restored" or "restore-failed";
            var id = value.Stage is "restored" or "restore-failed" ? "restore" : value.Stage;
            current = current with
            {
                Busy = !terminal && current.Busy,
                Steps = current.Steps.Select(s => s.Id == id ? s with
                {
                    Status = value.Stage == "restored" ? "已确认运行" : value.Stage == "restore-failed" ? "未确认" : value.Completed ? "通过" : "进行中",
                    Detail = value.Message
                }
                    : value.Stage == "ready" && s.Id is "symbols" or "snapshot" ? s with
                    {
                        Status = "通过"
                    } : s).ToArray()
            };
        }
        Changed?.Invoke();
    }

    private sealed class Reporter(DebugLaunchService owner) : IProgress<DebugStartupProgress>
    {
        public void Report(DebugStartupProgress value) => owner.Set(value);
    }
}
