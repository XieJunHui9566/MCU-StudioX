namespace StudioX.Packages;

using System.Text.RegularExpressions;
using StudioX.Foundation;

public static partial class PackValidator
{
    public static void Validate(PackManifest manifest, string root)
    {
        if (manifest.FormatVersion != 1) throw new StudioXException("PACK_FORMAT", "需要 StudioX Pack 格式 1；旧产品芯片包不能直接导入。");
        Token(manifest.Id); Version(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Vendor) || manifest.Devices is not { Count: > 0 })
            throw new StudioXException("PACK_MANIFEST", "芯片包缺少名称、厂商或器件。");
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in manifest.Devices)
        {
            Token(device.Id); Token(device.ToolsetId); Version(device.ToolsetVersion);
            if (!devices.Add(device.Id)) throw new StudioXException("PACK_DUPLICATE_DEVICE", "芯片型号重复。");
            if (device.Architecture is not ("arm" or "riscv" or "mcs51") || device.FlashBytes == 0 || device.RamBytes == 0 ||
                (ulong)device.FlashOrigin + device.FlashBytes > 0x100000000UL || (ulong)device.RamOrigin + device.RamBytes > 0x100000000UL)
                throw new StudioXException("PACK_DEVICE", "芯片架构或内存定义无效。");
            if (device.Architecture == "mcs51" && (device.ToolsetId != "stc.sdcc" || device.CompilerId != "sdcc-4.5.0-15242" ||
                device.LinkerScript != "" || device.OpenOcd is not null))
                throw new StudioXException("PACK_DEVICE", "MCS-51 器件仅支持无调试配置的 STC SDCC 工具集，且不使用 GCC 链接脚本。");
            if (string.IsNullOrWhiteSpace(device.CompilerId) || device.CpuFlags is null || device.Defines is null ||
                device.Sources is null || device.IncludeDirectories is null || device.CompileOptions is null || device.LinkOptions is null)
                throw new StudioXException("PACK_BUILD", "器件构建信息不完整。");
            foreach (var flag in device.CpuFlags.Concat(device.Defines).Concat(device.CompileOptions).Concat(device.LinkOptions))
                if (string.IsNullOrEmpty(flag) || flag.Any(c => c is '\n' or '\r' or '\0' or ';' or '$' or '"' or '\\'))
                    throw new StudioXException("PACK_BUILD_VALUE", "构建参数包含不支持的字符。");
            if (device.Architecture != "mcs51") RequireFile(root, device.LinkerScript);
            foreach (var source in device.Sources) RequireFile(root, source);
            foreach (var include in device.IncludeDirectories)
                if (!Directory.Exists(PathBoundary.Resolve(root, include))) throw new StudioXException("PACK_INCLUDE", $"包含目录不存在：{include}");
            var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (device.Templates is not { Count: > 0 }) throw new StudioXException("PACK_TEMPLATE", "器件没有工程模板。");
            foreach (var template in device.Templates)
            {
                Token(template.Id);
                if (!templates.Add(template.Id)) throw new StudioXException("PACK_TEMPLATE", "模板 ID 重复。");
                RequireFile(root, template.EntryFile);
                if (template.Build is { } build)
                {
                    if (build.Defines is null || build.IncludeDirectories is null || build.Sources is null || build.CompileOptions is null || build.LinkOptions is null)
                        throw new StudioXException("PACK_TEMPLATE", "模板构建信息不完整。");
                    foreach (var flag in build.Defines.Concat(build.CompileOptions).Concat(build.LinkOptions))
                        if (string.IsNullOrEmpty(flag) || flag.Any(c => c is '\n' or '\r' or '\0' or ';' or '$' or '"' or '\\'))
                            throw new StudioXException("PACK_BUILD_VALUE", "模板构建参数包含不支持的字符。");
                    foreach (var source in build.Sources) RequireFile(root, source);
                    foreach (var include in build.IncludeDirectories)
                        if (!Directory.Exists(PathBoundary.Resolve(root, include))) throw new StudioXException("PACK_INCLUDE", "模板包含目录不存在：" + include);
                }
                if (template.Files is not null)
                {
                    var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src/main.c" };
                    foreach (var (destination, source) in template.Files)
                    {
                        _ = PathBoundary.Resolve(root, destination);
                        if (!(destination.StartsWith("src/", StringComparison.Ordinal) || destination.StartsWith("include/", StringComparison.Ordinal)) || !destinations.Add(destination))
                            throw new StudioXException("PACK_TEMPLATE_FILE", "模板用户文件必须放在 src/ 或 include/ 中，且不能重名。");
                        RequireFile(root, source);
                    }
                }
            }
            if (device.OpenOcd is { } openOcd)
            {
                if (openOcd.ApplicationFlashBytes is { } size && (size == 0 || size > device.FlashBytes))
                    throw new StudioXException("PACK_FLASH", "应用 Flash 范围必须大于零且不超过器件物理容量。");
                RequireFile(root, openOcd.TargetScript);
                if (openOcd.Probes is not { Count: > 0 }) throw new StudioXException("PACK_PROBE", "缺少烧录器配置。");
                var probes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var probe in openOcd.Probes)
                {
                    Token(probe.Id);
                    _ = PathBoundary.Resolve(root, probe.InterfaceScript);
                    if (!probes.Add(probe.Id) || !probe.InterfaceScript.StartsWith("interface/", StringComparison.Ordinal) ||
                        probe.Transport is not ("swd" or "jtag" or "hla_swd" or "dapdirect_swd" or "sdi") || probe.DefaultSpeedKhz is < 100 or > 15000)
                        throw new StudioXException("PACK_PROBE", "烧录器标识、接口、传输方式或默认速度无效。");
                    if (probe.Transport == "sdi")
                    {
                        if (device.Architecture != "riscv" || device.ToolsetId != "wch.riscv" || probe.Id != "wch-link" || probe.DefaultSpeedKhz is not (400 or 4000 or 6000))
                            throw new StudioXException("PACK_PROBE", "SDI 配置需要 WCH RISC-V 工具集和有效的 WCH-Link 速度。");
                        RequireFile(root, probe.InterfaceScript);
                    }
                }
            }
        }
    }

    public static void Token(string? value)
    {
        if (value is null || !SafeToken().IsMatch(value)) throw new StudioXException("ID_INVALID", $"标识符无效：{value}");
    }
    public static void Version(string? value)
    {
        if (value is null || !ExactVersion().IsMatch(value)) throw new StudioXException("VERSION_INVALID", "版本必须是明确的 major.minor.patch。");
    }
    private static void RequireFile(string root, string path)
    {
        if (!File.Exists(PathBoundary.Resolve(root, path))) throw new StudioXException("PACK_FILE_MISSING", $"芯片包缺少文件：{path}");
    }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)] private static partial Regex SafeToken();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)] private static partial Regex ExactVersion();
}
