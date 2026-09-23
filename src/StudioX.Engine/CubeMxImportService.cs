namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed record CubeMxInspection(string Directory, string Name, string Device, string IocFile,
    string ToolchainFile, IReadOnlyList<string> ConfigurePresets);

/// <summary>原位接入 CubeMX CMake 工程；仅添加 StudioX 元数据，不重写厂商生成文件。</summary>
public sealed class CubeMxImportService(ToolsetCatalog catalog)
{
    public const string ToolsetId = "arm.gnu";
    public const string ToolsetVersion = "1.0.0";
    public const string CompilerId = "arm-gnu-15.2.rel1";
    public Task<CubeMxInspection> InspectAsync(string directory, CancellationToken token = default, IProgress<string>? progress = null)
        => Task.Run(() => InspectInBackgroundAsync(directory, token, progress), token);
    private async Task<CubeMxInspection> InspectInBackgroundAsync(string directory, CancellationToken token, IProgress<string>? progress)
    {
        progress?.Report("读取 CubeMX 器件与工程信息…");
        var root = Path.GetFullPath(directory);
        // 当前随包 Binutils 的 objcopy 无法可靠处理非 ASCII 的绝对路径，提前给出可操作诊断。
        ValidatePath(root);
        foreach (var required in new[] { "CMakeLists.txt", "cmake/stm32cubemx/CMakeLists.txt", "cmake/gcc-arm-none-eabi.cmake" })
            if (!File.Exists(PathBoundary.Resolve(root, required)))
                throw new StudioXException("CUBEMX_LAYOUT", $"缺少 {required}。请在 CubeMX 中选择 CMake 工具链、生成代码，并选择工程根目录。");
        var iocs = Directory.GetFiles(root, "*.ioc", SearchOption.TopDirectoryOnly);
        if (iocs.Length != 1) throw new StudioXException("CUBEMX_IOC", "工程根目录需要恰好一个 CubeMX .ioc 文件。");
        var values = (await File.ReadAllLinesAsync(iocs[0], token)).Where(line => !line.StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2)).GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
        var name = values.GetValueOrDefault("ProjectManager.ProjectName", Path.GetFileNameWithoutExtension(iocs[0]));
        var device = values.GetValueOrDefault("Mcu.CPN", values.GetValueOrDefault("Mcu.Name", ""));
        if (!device.StartsWith("STM32", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new StudioXException("CUBEMX_IDENTITY", ".ioc 中缺少有效的 STM32 器件或工程名称。");
        var presets = new List<string>();
        if (File.Exists(Path.Combine(root, "CMakePresets.json")) || File.Exists(Path.Combine(root, "CMakeUserPresets.json")))
        {
            // 让 CMake 自己处理预设继承、include 和条件，避免自行猜测预设含义。
            var tools = await catalog.ResolveAsync(ToolsetId, ToolsetVersion, CompilerId, token, progress: progress);
            progress?.Report("读取 CMake 配置预设…");
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("cmake"), ["--list-presets=configure"], root,
                TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
            if (!result.Success) throw new StudioXException("CUBEMX_PRESETS", "无法读取 CMake 预设：\n" + result.StandardOutput + result.StandardError);
            foreach (Match match in Regex.Matches(result.StandardOutput, "^\\s+\"([^\"]+)\"", RegexOptions.Multiline)) presets.Add(match.Groups[1].Value);
            if (presets.Count == 0) throw new StudioXException("CUBEMX_PRESETS", "CMake 没有可用的配置预设，请检查预设条件与 hidden 设置。");
        }
        return new(root, name, device, Path.GetFileName(iocs[0]), "cmake/gcc-arm-none-eabi.cmake", presets);
    }

    public Task<ProjectManifest> ImportAsync(string directory, string? configurePreset = null, string buildType = "Debug", CancellationToken token = default,
        IProgress<string>? progress = null)
        => Task.Run(() => ImportInBackgroundAsync(directory, configurePreset, buildType, token, progress), token);
    private async Task<ProjectManifest> ImportInBackgroundAsync(string directory, string? configurePreset, string buildType, CancellationToken token,
        IProgress<string>? progress)
    {
        var inspection = await InspectAsync(directory, token, progress);
        if (inspection.ConfigurePresets.Count > 0 && (configurePreset is null || !inspection.ConfigurePresets.Contains(configurePreset)))
            throw new StudioXException("CUBEMX_PRESET", "请选择该工程实际提供的 CMake 配置预设。");
        if (inspection.ConfigurePresets.Count == 0 && configurePreset is not null)
            throw new StudioXException("CUBEMX_PRESET", "工程没有 CMake 配置预设。");
        var project = new ProjectManifest(1, inspection.Name, "", "", "", inspection.Device, "", ToolsetId, ToolsetVersion, CompilerId,
            ProjectKind.CubeMx, new(inspection.IocFile, inspection.ToolchainFile, configurePreset, buildType));
        Validate(inspection.Directory, project);
        progress?.Report("保存 CubeMX 导入信息…");
        var path = PathBoundary.Resolve(inspection.Directory, ".studiox/project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 原有工程元数据永不覆盖；使用临时文件原子写入，失败时不留下半份工程配置。
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(project, JsonStore.Options), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return project;
    }

    internal static void Validate(string directory, ProjectManifest project)
    {
        ValidatePath(Path.GetFullPath(directory));
        if (project.CubeMx is not { } settings || string.IsNullOrWhiteSpace(project.Name) || project.Name.Any(char.IsControl) ||
            settings.BuildType is not ("Debug" or "Release" or "RelWithDebInfo" or "MinSizeRel") ||
            settings.ConfigurePreset is { } preset && (string.IsNullOrWhiteSpace(preset) || preset.Any(char.IsControl)))
            throw new StudioXException("CUBEMX_SETTINGS", "CubeMX 工程配置无效。");
        PathBoundary.Resolve(directory, settings.IocFile);
        PathBoundary.Resolve(directory, settings.ToolchainFile);
        if (project.ToolsetId != ToolsetId || project.ToolsetVersion != ToolsetVersion || project.CompilerId != CompilerId)
            throw new StudioXException("CUBEMX_TOOLSET", "CubeMX 工程需要此版本支持的内置 ARM GNU 工具集。");
    }
    private static void ValidatePath(string path)
    {
        if (path.Any(character => character > 127))
            throw new StudioXException("CUBEMX_PATH", "当前内置 ARM GNU 固件转换工具不支持中文等非 ASCII 工程路径，请先将工程移到英文路径（可以包含空格）再导入。");
    }
}
