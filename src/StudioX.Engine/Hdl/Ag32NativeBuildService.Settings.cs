namespace StudioX.Engine.Hdl;

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class Ag32NativeBuildService
{
    private const string ToolLockPath = ".studiox/ag32-logic-toolchain.lock.json";
    public async Task<Ag32NativeBuildSettings> ReadSettingsAsync(string root, CancellationToken token = default)
    {
        var project = await HdlSchematicInputs.RequireProjectAsync(root, token);
        var path = PathBoundary.Resolve(root, Ag32NativeBuildSettings.RelativePath);
        if (File.Exists(path)) return await JsonStore.ReadAsync<Ag32NativeBuildSettings>(path, token);
        // 自动发现只作初始值；厂商生成物和 testbench 不进入实际硬件综合。
        var sources = HdlSchematicInputs.DiscoverSources(root, project.Logic!.VerilogFile)
            .Where(source => !source.Split('/').Any(part => part is "alta_db" or "logic_db" or "sim" or "tb"))
            .Where(source => Path.GetFileName(source) is not ("pins.v" or "pins_routed.v" or "user_logic_tmpl.v"))
            .Where(source => !Path.GetFileName(source).StartsWith("tb_", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(1, sources, ["logic"], [], []);
    }

    public async Task SaveSettingsAsync(string root, Ag32NativeBuildSettings settings, CancellationToken token = default)
    {
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        ValidateSettings(root, settings);
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, Ag32NativeBuildSettings.RelativePath), settings, token);
        Invalidate(root);
    }

    private static void ValidateSettings(string root, Ag32NativeBuildSettings settings)
    {
        if (settings.FormatVersion != 1 || settings.SdcFiles is null || settings.Sources is null ||
            settings.IncludeDirectories is null || settings.Defines is null)
            throw new StudioXException("AG32_LOGIC_SETTINGS", "逻辑构建配置格式应为 1。");
        HdlSchematicInputs.Validate(root, settings.Inputs);
        if (!settings.Sources.Contains("logic/user_logic.v", StringComparer.OrdinalIgnoreCase))
            throw new StudioXException("AG32_LOGIC_ENTRY", "逻辑构建必须包含 logic/user_logic.v，模块名为 user_logic。");
        foreach (var file in settings.SdcFiles)
            if (!File.Exists(PathBoundary.Resolve(root, file)) || Path.GetExtension(file) != ".sdc")
                throw new StudioXException("AG32_LOGIC_SDC", "时序约束不存在或不是 .sdc：" + file);
    }

    private static async Task RequireDeviceAsync(string root, ProjectManifest project, CancellationToken token)
    {
        var profile = Ag32DeviceCatalog.Require(project.DeviceId);
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || pack.Vendor != "AGM" ||
            device is null || !profile.Matches(device) || device.ToolsetId != project.ToolsetId ||
            device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId ||
            project.Logic != new Ag32LogicProjectSettings(profile.TargetDevice, "logic/user_logic.v", "logic/pins.ve"))
            throw new StudioXException("AG32_LOGIC_DEVICE", "自定义逻辑的器件、封装、容量和工具必须匹配已核实的 AGM 器件包。");
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    public static void Invalidate(string root)
    {
        var path = PathBoundary.Resolve(root, Ag32NativeBuildReceipt.RelativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<string> ReportPathAsync(string root, string report, CancellationToken token = default)
    {
        if (report is not ("setup.rpt" or "hold.rpt" or "fmax.rpt" or "coverage.rpt"))
            throw new StudioXException("AG32_LOGIC_REPORT", "请选择 setup、hold、Fmax 或覆盖率报告。");
        var image = await RequireImageAsync(root, token);
        return Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(image.Path)!, report)).Replace('\\', '/');
    }

    public async Task<Ag32PinMappingImage> RequireImageAsync(string root, CancellationToken token = default)
    {
        var project = await HdlSchematicInputs.RequireProjectAsync(root, token);
        await RequireDeviceAsync(root, project, token);
        var path = PathBoundary.Resolve(root, Ag32NativeBuildReceipt.RelativePath);
        if (!File.Exists(path)) throw new StudioXException("AG32_LOGIC_STALE", "自定义逻辑尚未成功联合构建，请先编译。");
        var receipt = await JsonStore.ReadAsync<Ag32NativeBuildReceipt>(path, token);
        var mapping = await catalog.ResolveAsync("agm.pin-mapping", "1.0.0", "agm.ve", token);
        var native = await catalog.ResolveAsync("agm.logic", "1.0.0", "agm.native", token);
        if (receipt.FormatVersion != 1 || receipt.Inputs is null || receipt.Artifacts is null || receipt.Settings is null ||
            receipt.MappingTools != mapping.Fingerprint || receipt.NativeTools != native.Fingerprint ||
            !receipt.Inputs.ContainsKey(ToolLockPath) ||
            receipt.ImageRelativePath is null || !Regex.IsMatch(receipt.ImageRelativePath, @"^\.build/ag32-logic/[a-f0-9]{32}/pins\.bin$") ||
            !receipt.Artifacts.ContainsKey(receipt.ImageRelativePath))
            throw new StudioXException("AG32_LOGIC_STALE", "逻辑工具或凭据已变化，请重新编译。");
        ValidateSettings(root, receipt.Settings);
        foreach (var (relative, expected) in receipt.Inputs.Concat(receipt.Artifacts))
        {
            var file = PathBoundary.Resolve(root, relative);
            if ((File.Exists(file) ? await HashAsync(file, token) : "") != expected)
                throw new StudioXException("AG32_LOGIC_STALE", "逻辑输入或产物已变化，请重新编译：" + relative);
        }
        if (!await InputsCurrentAsync(root, receipt.Inputs, receipt.Settings, token))
            throw new StudioXException("AG32_LOGIC_STALE", "逻辑源文件集合已变化，请重新编译。");
        var image = PathBoundary.Resolve(root, receipt.ImageRelativePath);
        if (new FileInfo(image).Length is <= 0 or > 102400)
            throw new StudioXException("AG32_LOGIC_IMAGE", "逻辑镜像必须位于保留的 100 KiB 区域内。");
        return new(image, receipt.Artifacts[receipt.ImageRelativePath], new FileInfo(image).Length,
            receipt.SourceDigest, native.Fingerprint, Ag32DeviceCatalog.Require(project.DeviceId).LogicImageAddress!.Value);
    }

    private static Task<bool> InputsCurrentAsync(string root, Dictionary<string, string> hashes, Ag32NativeBuildSettings settings, CancellationToken token) =>
        HdlSchematicInputs.IsCurrentAsync(root, hashes.Where(pair => pair.Key != Ag32NativeBuildSettings.RelativePath &&
            pair.Key != ToolLockPath && pair.Key != "logic/pins.ve" && !settings.SdcFiles.Contains(pair.Key)).ToDictionary(), settings.Inputs, token);
}
