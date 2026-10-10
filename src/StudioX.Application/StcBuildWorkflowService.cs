namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Foundation;

/// <summary>按用户选择的普通下载或监控调试准备构建，避免两种擦写通道共用旧产物。</summary>
public sealed class StcBuildWorkflowService(BuildService builds, Func<bool> debugActive)
{
    private const string PreferencePath = ".studiox/stc-workflow.json";
    private sealed record OrdinaryPreferences(int? CodeRomSizeBytes, CompilerDebugInfo DebugInfo);
    public Task<ProjectBuildSettings> PrepareDownloadAsync(string directory, CancellationToken token = default)
        => PrepareAsync(directory, false, token);

    public Task<ProjectBuildSettings> PrepareDebugAsync(string directory, CancellationToken token = default)
        => PrepareAsync(directory, true, token);

    private async Task<ProjectBuildSettings> PrepareAsync(string directory, bool debugging, CancellationToken token)
    {
        if (debugActive())
        {
            throw new StudioXException("STC_BUILD_SESSION", "请先结束当前调试，再准备下载或新的调试构建。");
        }
        var project = await ProjectService.ReadAsync(directory, token);
        if (project.ToolsetId != "stc.sdcc" || debugging && project.DeviceId != "IAP15F2K61S2")
        {
            throw new StudioXException("STC_BUILD_TARGET", "普通下载仅适用于 STC SDCC 工程；Mon51 调试目前仅适用于 IAP15F2K61S2。");
        }
        var settings = await builds.LoadSettingsAsync(directory, token);
        var preferencePath = PathBoundary.Resolve(directory, PreferencePath);
        if (debugging && !settings.Mon51Profile)
            {await JsonStore.WriteAsync(preferencePath, new OrdinaryPreferences(settings.CodeRomSizeBytes, settings.DebugInfo), token);}
        var ordinary = !debugging && settings.Mon51Profile && File.Exists(preferencePath)
            ? await JsonStore.ReadAsync<OrdinaryPreferences>(preferencePath, token) : null;
        // 保留用户优化；监控构建限制实际可用内存并要求标准 CDB。
        var selected = debugging
            ? settings with { Mon51Profile = true, DebugInfo = CompilerDebugInfo.Standard,
                CodeRomSizeBytes = Math.Min(settings.CodeRomSizeBytes ?? 0xdbfd, 0xdbfd) }
            : settings with { Mon51Profile = false, CodeRomSizeBytes = ordinary?.CodeRomSizeBytes ?? (ordinary is null ? settings.CodeRomSizeBytes : null),
                DebugInfo = ordinary?.DebugInfo ?? settings.DebugInfo };
        await builds.SaveSettingsAsync(directory, selected, token);
        return selected;
    }
}
