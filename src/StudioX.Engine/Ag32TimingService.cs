namespace StudioX.Engine;

using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>重新核对实际报告与工程输入；未验证、失败和过期证据不显示为通过。</summary>
public sealed class Ag32TimingService(ToolsetCatalog tools)
{
    public async Task<Ag32TimingStatus> ReadAsync(string directory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        try
        {
            var project = await ProjectService.ReadAsync(root, token);
            if (project.PinMapping is not { } settings || project.Logic is not null)
            {
                return new(Ag32TimingState.Unverified, "本页只显示基础映射的实际时序；自定义逻辑使用自己的构建报告。");
            }
            var source = PathBoundary.Resolve(root, settings.PinMapFile);
            var sourceHash = await Ag32TimingEvidence.HashAsync(source, token);
            var projectHash = await Ag32TimingEvidence.HashAsync(PathBoundary.Resolve(root, ".studiox/project.json"), token);
            var document = new Ag32PinPlanDocument(await File.ReadAllBytesAsync(source, token));
            var clocks = document.Clocks;
            var attempt = PathBoundary.Resolve(root, Ag32TimingEvidence.RelativePath);
            var receipt = PathBoundary.Resolve(root, Ag32PinMappingReceipt.RelativePath);
            var build = PathBoundary.Resolve(root, ".build/ag32-mapping");
            Ag32PinMappingTimingReport report;
            if (File.Exists(attempt))
            {
                if (new FileInfo(attempt).Length > 64 * 1024)
                {
                    throw new IOException("时序记录超过大小限制。");
                }
                var evidence = await JsonStore.ReadAsync<Ag32TimingEvidence>(attempt, token);
                var resolved = await tools.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
                if (evidence.Version != 1 || evidence.ProjectSha256 != projectHash || evidence.SourceSha256 != sourceHash ||
                    evidence.ToolFingerprint != resolved.Fingerprint)
                {
                    return new(Ag32TimingState.Stale, "结果已过期 / Stale：工程、引脚、时钟或工具已变化，请重新编译。", clocks);
                }
                if (!await evidence.InputsMatchAsync(root, token))
                {
                    return new(Ag32TimingState.Stale, "结果已过期 / Stale：布线或约束文件已变化，请重新编译。", clocks);
                }
                report = await Ag32PinMappingTimingReport.ReadAsync(build, token, requirePassing: false);
                if (report.Sha256 != evidence.ReportSha256)
                {
                    return new(Ag32TimingState.Stale, "结果已过期 / Stale：时序报告与当次构建不一致，请重新编译。", clocks);
                }
            }
            else if (File.Exists(receipt))
            {
                // 兼容已有成功凭据，但没有凭据的旧失败报告不能证明与当前配置对应。
                await new Ag32PinMappingBuildService(tools).ValidateBuiltAsync(root, token);
                report = await Ag32PinMappingTimingReport.ReadAsync(build, token);
            }
            else
            {
                return new(Ag32TimingState.Unverified, "尚未验证 / Unverified：尚无当前配置的布局布线结果。分频合法不代表时序通过，请先编译。", clocks);
            }

            if (!report.Failed)
            {
                await new Ag32PinMappingBuildService(tools).ValidateBuiltAsync(root, token);
            }
            if (sourceHash != await Ag32TimingEvidence.HashAsync(source, token) ||
                projectHash != await Ag32TimingEvidence.HashAsync(PathBoundary.Resolve(root, ".studiox/project.json"), token))
            {
                return new(Ag32TimingState.Stale, "结果已过期 / Stale：读取期间配置发生变化，请刷新后重新编译。", clocks);
            }

            var low = report.WorstSetupSlackNs is >= 0 and < Ag32ClockPolicy.LowMarginThresholdNs ||
                report.WorstHoldSlackNs is >= 0 and < Ag32ClockPolicy.LowMarginThresholdNs;
            return new(report.Failed ? Ag32TimingState.Failed : low ? Ag32TimingState.LowMargin : Ag32TimingState.Passed,
                report.Failed ? "时序未满足 / Timing failed：不能下载此镜像。请降低频率或优化逻辑后重新编译。"
                : low ? $"时序通过，但余量偏小 / Low margin：最小余量低于 {Ag32ClockPolicy.LowMarginThresholdNs} ns 提醒阈值。"
                : "当前配置时序通过 / Timing passed。",
                clocks, report.WorstSetupSlackNs, report.WorstHoldSlackNs, report.Covered, report.Total,
                report.SetupPath, report.HoldPath, ".build/ag32-mapping/logic_log.txt");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is StudioXException or IOException or JsonException or UnauthorizedAccessException or DecoderFallbackException or OverflowException)
        {
            return new(Ag32TimingState.Unverified, "时序证据不可用 / Unverified：" + error.Message);
        }
    }
}
