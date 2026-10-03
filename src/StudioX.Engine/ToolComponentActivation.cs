namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>只在用户数据中记录明确禁用的版本；不修改组件清单、工程或系统环境。</summary>
internal sealed class ToolComponentActivation(string userDataDirectory)
{
    private sealed record State(int FormatVersion, string[] Disabled);
    private string StatePath => Path.Combine(userDataDirectory, "development-component-activation.json");
    public bool IsEnabled(string id, string version) => !Read().Disabled.Contains(id + "/" + version + "/win-x64", StringComparer.Ordinal);
    private State Read()
    {
        if (!File.Exists(StatePath)) return new(1, []);
        if (new FileInfo(StatePath).Length > 256 * 1024) throw new StudioXException("TOOLS_ACTIVATION", "组件启用记录过大，停止运行工具。");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), JsonStore.Options);
        if (state is not { FormatVersion: 1, Disabled: not null } || state.Disabled.Length > 2048)
            throw new StudioXException("TOOLS_ACTIVATION", "组件启用记录格式无效，停止运行工具。");
        foreach (var key in state.Disabled)
        {
            var parts = key.Split('/');
            if (parts.Length != 3 || parts[2] != "win-x64") throw new StudioXException("TOOLS_ACTIVATION", "组件启用记录身份无效。");
            PackValidator.Token(parts[0]); PackValidator.Version(parts[1]);
        }
        return state;
    }
    public async Task SetEnabledAsync(string id, string version, bool enabled, CancellationToken token)
    {
        // 不同组件同时切换也需要互斥，避免原子写入覆盖另一进程刚保存的状态。
        using var lease = ToolUsageLease.Acquire(StatePath, maintenance: true);
        var key = id + "/" + version + "/win-x64";
        var current = Read().Disabled;
        var next = enabled ? current.Where(item => item != key) : current.Append(key);
        await JsonStore.WriteAsync(StatePath, new State(1, next.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()), token);
    }
}
