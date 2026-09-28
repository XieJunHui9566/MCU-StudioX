namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

public sealed partial class OpenOcdService
{
    private async Task<(DownloadImagePreview Preview, byte[] Bytes)?> ValidatePinMappingImageAsync(
        string root, ProjectManifest project, DownloadConfiguration configuration, CancellationToken token)
    {
        if (project.PinMapping is null && project.Logic is null)
        {
            return null;
        }
        RequirePinMappingTarget(configuration);
        _ = Ag32PinMappingTargetScript.RequireCompatible(root);
        var mapping = project.Logic is not null
            ? await new Hdl.Ag32NativeBuildService(catalog).RequireImageAsync(root, token)
            : await new Ag32PinMappingBuildService(catalog).RequireImageAsync(root, token);
        var bytes = await File.ReadAllBytesAsync(mapping.Path, token);
        if (bytes.Length == 0 || bytes.Length > Ag32LogicWorkflowService.ReservedLogicBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), mapping.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("AG32_MAPPING_IMAGE_CHANGED", "引脚映射镜像已变化，请重新编译后下载。");
        }
        return (new(Path.GetRelativePath(root, mapping.Path).Replace('\\', '/'), "bin", mapping.Sha256,
            bytes.LongLength, 0x80027000, "pin-mapping"), bytes);
    }

    /// <summary>MCU 与基础映射共同写入，保留现有逻辑区地址和所有选项字节。</summary>
    public static string[] CreatePinMappingArguments(string projectDirectory, DownloadConfiguration configuration,
        DownloadOptions options, ResolvedToolset tools, IReadOnlyList<DownloadImageSnapshot> images)
    {
        configuration = Ag32ProbeConfiguration.Normalize(configuration);
        options = Ag32ProbeConfiguration.NormalizeOptions(configuration.Device, options);
        RequirePinMappingTarget(configuration);
        if (images.Count != 2 || images[0].Preview.Role != "application" || images[1].Preview.Role != "pin-mapping" ||
            images[0].Preview.Address != 0x80000000 || images[0].Preview.Format != "bin" ||
            images[0].Preview.Bytes is <= 0 or > 0x27000 || images[1].Preview.Address != 0x80027000 ||
            images[1].Preview.Format != "bin" || images[1].Preview.Bytes is <= 0 or > 0x19000 ||
            images.Any(image => image.VerificationBytes != (ulong)image.Preview.Bytes))
        {
            throw new StudioXException("AG32_MAPPING_LAYOUT", "AG32 下载布局必须为前 156 KiB MCU 应用和末尾 100 KiB 基础映射。");
        }
        var target = Ag32PinMappingTargetScript.RequireCompatible(projectDirectory);
        var arguments = CreateArguments(projectDirectory, configuration with
        {
            TargetScriptText = target
        }, options, tools, images[0].Path).ToList();
        // 沿用同一目标脚本的身份、容量、读保护和逻辑地址检查；不用会改选项字节的 write_fpga_config。
        // 标记放在固定 init 边界，失败时才能证明尚未触及任何映像写入。
        var operation = $"echo {OpenOcdDownloadDiagnostics.ConnectionBegin}; init; echo {OpenOcdDownloadDiagnostics.ConnectionReady}; reset halt; studiox_check_target; ";
        foreach (var image in images)
        {
            var path = TclString(image.Path.Replace('\\', '/'));
            var address = $"0x{image.Preview.Address:x8}";
            var marker = image.Preview.Role == "application" ? "APPLICATION" : "PIN_MAPPING";
            operation += $"echo STUDIOX_VERIFY_{marker}_BEGIN; echo {OpenOcdDownloadDiagnostics.FlashWriteBegin}; echo [flash write_image erase {path} {address} bin]; "
                + $"echo [verify_image {path} {address} bin]; echo STUDIOX_VERIFY_{marker}_END; ";
        }
        // 复位后逻辑控制器重新读取 Flash 配置，GPIO 才会使用本次新映射。
        operation += "reset run; echo STUDIOX_DOWNLOAD_VERIFIED; shutdown";
        arguments[^1] = operation;
        return arguments.ToArray();
    }

    private static void RequirePinMappingTarget(DownloadConfiguration configuration)
    {
        if (!Ag32ProbeConfiguration.IsSupported(configuration.Device) || configuration.Device.FlashOrigin != 0x80000000 ||
            configuration.Device.FlashBytes != 0x40000 || configuration.OpenOcd.ApplicationFlashBytes != 0x27000 ||
            configuration.TargetScriptText is not null || configuration.OpenOcd.TargetScript != "debug/ag32vf303.cfg" ||
            configuration.OpenOcd.Probes.Count is < 1 or > 2 || !configuration.OpenOcd.Probes.All(Ag32ProbeConfiguration.IsSupportedProbe))
        {
            throw new StudioXException("AG32_MAPPING_TARGET", "基础映射下载需要匹配器件包的 AG32VF303CCT6、DAP / J-Link SWD 和固定未压缩逻辑布局。");
        }
    }
}
