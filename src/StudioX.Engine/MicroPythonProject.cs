namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>脚本工程生成与身份校验；不会产生 CMake 配置或本机工具链锁。</summary>
public static class MicroPythonProject
{
    public static StudioXException NativeOperationUnavailable() => new("MICROPYTHON_NATIVE_OPERATION",
        "MicroPython 脚本由板上解释器运行。请点击下载按钮连接 REPL 或下载脚本；不使用 C 编译、OpenOCD 下载或 GDB 调试。");

    internal static async Task WriteAsync(InstalledPack pack, BuildPlan plan, string directory, CancellationToken token)
    {
        var template = plan.Device.Templates.Single(item => item.Id == plan.Project.TemplateId);
        Directory.CreateDirectory(Path.Combine(directory, "device"));
        // 仅保留身份记录及所选脚本，避免把整个 C SDK 复制到解释器工程。
        File.Copy(PathBoundary.Resolve(pack.RootDirectory, "manifest.json"), Path.Combine(directory, "device", "manifest.json"));
        File.Copy(PathBoundary.Resolve(pack.RootDirectory, template.EntryFile), Path.Combine(directory, "main.py"));
        foreach (var (destination, source) in template.Files ?? new Dictionary<string, string>())
        {
            var path = PathBoundary.Resolve(directory, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(PathBoundary.Resolve(pack.RootDirectory, source), path);
        }
        await JsonStore.WriteAsync(Path.Combine(directory, ".studiox", "project.json"), plan.Project, token);
        await File.WriteAllTextAsync(Path.Combine(directory, ".gitignore"),
            "__pycache__/\n*.pyc\n.studiox/micropython-backups/\n.studiox/micropython-session.json\n", token);
    }

    public static async Task ValidateAsync(string directory, ProjectManifest project, CancellationToken token = default)
    {
        PackValidator.Token(project.Name);
        PackValidator.Token(project.PackId);
        PackValidator.Version(project.PackVersion);
        if (project.Kind != ProjectKind.MicroPython || project.MicroPython is null || project.EntryFile != "main.py" ||
            project.ToolsetId != "" || project.ToolsetVersion != "" || project.CompilerId != "" ||
            project.CubeMx is not null || project.Espressif is not null || project.Zephyr is not null ||
            project.Logic is not null || project.PinMapping is not null)
        {
            throw new StudioXException("PROJECT_MICROPYTHON", "MicroPython 工程配置与创建记录不一致。");
        }
        project.MicroPython.Validate(project.DeviceId);
        var path = PathBoundary.Resolve(directory, "device/manifest.json");
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new StudioXException("PROJECT_MICROPYTHON", "工程器件记录超出大小限制。");
        }
        var manifest = await JsonStore.ReadAsync<PackManifest>(path, token);
        var device = manifest.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        var template = device?.Templates.SingleOrDefault(item => item.Id == project.TemplateId);
        if (manifest.FormatVersion != 1 || manifest.Id != project.PackId || manifest.Version != project.PackVersion ||
            template?.MicroPython != project.MicroPython)
        {
            throw new StudioXException("PROJECT_MICROPYTHON", "MicroPython 板型、模板或解释器版本与包记录不一致。");
        }
    }
}
