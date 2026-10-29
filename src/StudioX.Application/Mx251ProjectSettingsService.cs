namespace StudioX.Application;

using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>工程详情与构建共用 mx251.json，避免两份内存模型设置相互覆盖。</summary>
public sealed class Mx251ProjectSettingsService(BuildService builds, ProjectFileService files)
{
    public async Task<Mx251ProjectSettings> ReadAsync(string directory, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(directory, token);
        if (project.Kind != ProjectKind.Pack || project.ToolsetId != Mx251Profile.ToolsetId || project.CompilerId != Mx251Profile.CompilerId)
        {
            throw new StudioXException("MX251_SETTINGS_TARGET", "此设置仅用于 MX251 工程。");
        }
        var document = await files.ReadAsync(directory, Mx251Profile.ConfigurationFile, token);
        var config = Parse(document.Text);
        var model = Model(config);
        // 默认值取自工程随附的创建模板，旧 large 工程也保留自己的默认值。
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(directory, "device/manifest.json"), token);
        var template = pack.Devices.Single(d => d.Id == project.DeviceId).Templates.Single(t => t.Id == project.TemplateId);
        var path = PathBoundary.Resolve(Path.Combine(directory, "device"), template.Mx251Configuration
            ?? throw new StudioXException("MX251_CONFIGURATION", "创建模板缺少 MX251 配置。"));
        var defaultModel = Model(Parse(await File.ReadAllTextAsync(path, token)));
        return new(document, model, defaultModel);
    }

    public async Task<Mx251ProjectSettings> SaveModelAsync(string directory, Mx251ProjectSettings expected, string model, CancellationToken token = default)
    {
        using var maintenance = builds.AcquireMaintenance(token);
        var current = await ReadAsync(directory, token);
        if (current.Configuration.DiskHash != expected.Configuration.DiskHash)
        {
            throw new StudioXException("MX251_SETTINGS_CHANGED", "mx251.json 已被修改，请重新打开工程详情后再保存。");
        }
        var config = Parse(current.Configuration.Text);
        config["model"] = model;
        Model(config);
        var runtime = config["schema"]?.GetValue<int>() == 1
            ? (config["nativeMath"]?.GetValue<bool>() == true ? "native-math" : "standard")
            : config["runtime"]?.GetValue<string>();
        if (model == "large" && runtime != "standard")
        {
            throw new StudioXException("MX251_MODEL_RUNTIME", "当前运行库仅支持 small，不能切换到 large。请先在 mx251.json 中明确选择兼容的 standard 运行库及 ABI 组合。");
        }
        if (model == current.Model)
        {
            return current;
        }
        // 文件服务保留本地历史、编码并在替换前再次校验磁盘哈希。
        var saved = await files.SaveAsync(directory, current.Configuration, config.ToJsonString(JsonStore.Options) + "\n", token);
        foreach (var relative in new[] { ".build/studiox-build-receipt.json", ".build/studiox-memory.json", ".build/firmware.hex" })
        {
            var path = PathBoundary.Resolve(directory, relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        return current with
        {
            Configuration = saved,
            Model = model
        };
    }

    private static JsonObject Parse(string text)
    {
        var config = JsonNode.Parse(text) as JsonObject
            ?? throw new StudioXException("MX251_CONFIGURATION", "mx251.json 必须是配置对象。");
        if (config["schema"]?.GetValue<int>() is not (1 or 2) || config["target"]?.GetValue<string>() != "AI8051U-34K64-32")
        {
            throw new StudioXException("MX251_CONFIGURATION", "不支持此 MX251 配置版本或目标。");
        }
        return config;
    }

    private static string Model(JsonObject config) => config["model"]?.GetValue<string>() switch
    {
        "small" => "small",
        "large" => "large",
        _ => throw new StudioXException("MX251_MODEL", "内存模型必须为 small 或 large。")
    };
}
