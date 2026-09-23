namespace StudioX.Engine.Debugging;

using System.Security.Cryptography;
using StudioX.Foundation;

public sealed record HardwareDebugPreparation(string ProjectDirectory, DownloadConfiguration Configuration, ResolvedToolset Tools, string Elf, string LogPath);

public static class HardwareDebugPreparer
{
    public static Task<HardwareDebugPreparation> PrepareAsync(string project, OpenOcdService downloads, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var root = Path.GetFullPath(project);
            if ((await ProjectBuildSettings.ReadAsync(root, token)).DebugInfo == CompilerDebugInfo.None)
                throw new StudioXException("DEBUG_SYMBOLS", "当前工程选择了 -g0，不生成调试信息。请在工程编译参数中选择 -g2 或 -g3，重新编译并下载后调试。");
            var configuration = await downloads.ConfigurationAsync(root, token) ?? throw new StudioXException("DEBUG_TARGET", "当前工程缺少调试配置。");
            _ = OpenOcdDebugPlanner.ResolveProbe(configuration);
            var prepared = await downloads.PrepareAsync(root, configuration.Options, token);
            var receipt = await JsonStore.ReadAsync<BuildReceipt>(PathBoundary.Resolve(root, BuildReceipt.RelativePath), token);
            if (receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
                throw new StudioXException("DEBUG_STALE", "源码、构建脚本或编译参数已变化，请重新编译后调试。");
            var image = receipt.Images.Single();
            if (image.SymbolsPath is null || image.SymbolsSha256 is null) throw new StudioXException("DEBUG_SYMBOLS", "缺少 ELF 调试凭据，请重新编译。");
            var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, image.SymbolsPath), token);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(image.SymbolsSha256, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("DEBUG_SYMBOLS", "ELF 已变化，请重新编译。");
            FirmwareImage.Validate(bytes, "elf", configuration.Device);
            // 会话持有构建时校验过的独立符号文件；后续构建不能替换正在使用的 ELF。
            var directory = Path.GetDirectoryName(prepared.Image)!;
            var elf = Path.Combine(directory, "debug.elf"); await File.WriteAllBytesAsync(elf, bytes, token);
            return new HardwareDebugPreparation(root, configuration, prepared.Tools, elf, Path.Combine(directory, "debug-session.log"));
        }, token);
}
