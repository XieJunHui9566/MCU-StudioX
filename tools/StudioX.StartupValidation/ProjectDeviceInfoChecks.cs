using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class ProjectDeviceInfoChecks
{
    public static async Task RunAsync(string directory, PackManifest pack, Action<string> pass)
    {
        var project = await ProjectService.ReadAsync(directory);
        var path = Path.Combine(directory, "device", "manifest.json");
        var original = await File.ReadAllBytesAsync(path);
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Read-only project device metadata failed."); }
        try
        {
            var info = await ProjectDeviceInfo.ReadAsync(directory, project);
            Check(info.DeviceName == "Test device" && info.TemplateName == "Bare" && info.Notice == "");
            var afterRead = await File.ReadAllBytesAsync(path);
            Check(original.SequenceEqual(afterRead));
            // An unrelated/newer installed pack must never substitute for the project's recorded version.
            await JsonStore.WriteAsync(path, pack with { Version = "2.0.0", DisplayName = "Wrong version" });
            info = await ProjectDeviceInfo.ReadAsync(directory, project);
            Check(info.DeviceName == project.DeviceId && info.TemplateName == project.TemplateId && info.Notice.Length > 0);
            await JsonStore.WriteAsync(path, pack with { Devices = [] });
            Check((await ProjectDeviceInfo.ReadAsync(directory, project)).Notice.Length > 0);
            await File.WriteAllTextAsync(path, "{");
            Check((await ProjectDeviceInfo.ReadAsync(directory, project)).TemplateName == project.TemplateId);
            var missing = Path.Combine(directory, "nonexistent");
            Check((await ProjectDeviceInfo.ReadAsync(missing, project)).Notice.Length > 0);
            var cube = await ProjectDeviceInfo.ReadAsync(missing, project with { Kind = ProjectKind.CubeMx, TemplateId = "", PackId = "" });
            Check(cube.TemplateName == "STM32CubeMX 生成工程" && cube.PackName == "未使用 .mcupack" && cube.Notice == "");
            pass("PASS: project device/template details use exact local metadata, never modify it; missing/malformed/version-mismatched metadata and CubeMX imports display accurately");
        }
        finally { await File.WriteAllBytesAsync(path, original); }
    }
}
