namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>使用工程锁定工具离线解析已有日志/转储，不连接设备。</summary>
public sealed partial class FirmwareFaultService(ToolsetCatalog tools)
{
    private readonly ProcessRunner runner = new();
    private async Task<(ProjectManifest Project, ResolvedToolset Tools, string Elf, string Hash)> SymbolsAsync(string project, CancellationToken token)
    {
        var root = Path.GetFullPath(project);
        var manifest = await ProjectService.ReadAsync(root, token);
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(PathBoundary.Resolve(root, BuildReceipt.RelativePath), token);
        if (receipt.Project != manifest)
        {
            throw new StudioXException("FAULT_ELF", "工程配置与构建记录不同，请选择对应固件的工程。");
        }
        if (receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
        {
            throw new StudioXException("FAULT_ELF", "源码与构建记录不同，请使用故障固件对应的源码与 ELF。");
        }
        var images = receipt.Images.Where(i => i.SymbolsPath is not null).ToArray();
        if (images.Length != 1)
        {
            throw new StudioXException("FAULT_ELF", "构建记录需要包含唯一的应用程序 ELF。");
        }
        var image = images[0];
        var elf = PathBoundary.Resolve(root, image.SymbolsPath!);
        await using var stream = File.OpenRead(elf);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (!hash.Equals(image.SymbolsSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("FAULT_ELF", "ELF 与构建凭据不一致。");
        }
        var resolved = await tools.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId, token);
        if (manifest.Espressif is { } sdk)
        {
            resolved = resolved.ForEspressifTarget(sdk.Target);
        }
        return (manifest, resolved, elf, hash);
    }

    public Task<FaultAnalysisReport> SymbolizeAsync(string project, FaultAnalysisReport report, CancellationToken token = default)
        => Task.Run(async () =>
        {
            if (report.Addresses.Count == 0)
            {
                return report;
            }
            var symbols = await SymbolsAsync(project, token);
            if (!symbols.Project.DeviceId.Equals(report.Evidence.Device, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("FAULT_DEVICE", "现场器件与当前工程不一致。");
            }
            var args = new List<string> { "--nx", "--batch", "-iex", "set auto-load off", "-iex", "set may-call-functions off", symbols.Elf };
            foreach (var address in report.Addresses.Take(64))
            {
                args.AddRange(["-ex", $"info line *0x{address:x8}", "-ex", $"info symbol 0x{address:x8}"]);
            }
            var result = await runner.RunAsync(new(symbols.Tools.Tool("gdb"), args, project, TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(symbols.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
            var text = result.StandardOutput + "\n" + result.StandardError;
            if (result.ExitCode != 0 || result.TimedOut || result.OutputTruncated)
            {
                throw new StudioXException("FAULT_SYMBOLS", "离线地址解析失败：\n" + text);
            }
            return report with
            {
                ElfSha256 = symbols.Hash,
                SymbolInformation = text
            };
        }, token);

}
