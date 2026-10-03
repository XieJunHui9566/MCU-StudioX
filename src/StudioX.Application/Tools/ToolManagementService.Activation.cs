namespace StudioX.Application.Tools;

using StudioX.Foundation;
using StudioX.Engine;

public sealed partial class ToolManagementService
{
    public Task SetEnabledAsync(ManagedToolVersion selected, bool enabled, string? currentProject, CancellationToken token = default)
        => Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                EnsureIdle();
                var fresh = await FindFreshAsync(selected, currentProject, token);
                if (!fresh.CanToggle) throw new StudioXException("TOOLS_PROTECTED", "请选择未占用且身份检查完整的已安装组件。");
                if (fresh.Enabled != selected.Enabled) throw new StudioXException("TOOLS_CHANGED", "启用状态发生变化，请刷新后重试。");
                await catalog.SetEnabledAsync(fresh.Id, fresh.Version, enabled, token);
            }
            finally { gate.Release(); }
        }, token);

    /// <summary>只运行固定版本查询；不启动调试会话、不探测端口、不连接硬件。</summary>
    public Task<string> DiagnoseDebugEnvironmentAsync(ManagedToolVersion selected, string? currentProject,
        IProgress<string>? progress = null, CancellationToken token = default) => Task.Run(async () =>
    {
        EnsureIdle();
        if (!selected.Installed) throw new StudioXException("TOOLS_SELECTION", "请先恢复组件，再检查调试环境。");
        using var lease = ToolUsageLease.Acquire(VersionRoot(selected));
        var resolved = await catalog.ResolveAsync(selected.Id, selected.Version, selected.CompilerId, token, true, progress);
        string? target = null;
        if (currentProject is not null)
        {
            var project = await ProjectService.ReadAsync(currentProject, token);
            if (project.ToolsetId == selected.Id && project.ToolsetVersion == selected.Version)
                target = project.Espressif?.Target;
        }
        if (target is not null) resolved = resolved.ForEspressifTarget(target);
        var lines = new List<string> { $"调试环境检查：{selected.Id} / {selected.Version}", "组件内容校验通过。", "本检查只查询开发环境组件版本，不连接设备。" };
        var roles = new[] { "gcc", "gdb", "openocd" }.Concat(target is null
            ? resolved.Manifest.Executables.Keys.Where(role => role.StartsWith("gdb-", StringComparison.Ordinal)) : []).Distinct(StringComparer.Ordinal).ToArray();
        var count = 0;
        foreach (var role in roles)
        {
            if (!resolved.Manifest.Executables.ContainsKey(role)) continue;
            token.ThrowIfCancellationRequested();
            progress?.Report("检查 " + role + " --version…");
            var result = await new ProcessRunner().RunAsync(new(resolved.Tool(role), role.StartsWith("gdb", StringComparison.Ordinal) ? ["--nx", "--nh", "--version"] : ["--version"], resolved.RootDirectory,
                TimeSpan.FromSeconds(20), ToolsetEnvironment.Create(resolved), RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
            lines.Add($"\n[{role}] exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}\n" + result.StandardOutput + result.StandardError);
            if (!result.Success) throw new StudioXException("TOOLS_DEBUG_DIAGNOSTIC", string.Join('\n', lines));
            count++;
        }
        lines.Add(count == 0 ? "此组件未声明 GCC/GDB/OpenOCD 入口；调试能力由器件包及独立调试组件提供。"
            : "已检查工具入口。打开工程后从调试菜单选择探针和会话；本结果不代表实板连接成功。");
        return string.Join('\n', lines);
    }, token);
}
