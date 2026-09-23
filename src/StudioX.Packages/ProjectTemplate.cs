namespace StudioX.Packages;

/// <summary>模板入口复制到用户 src 目录；公共设备资源仍归芯片包。</summary>
public sealed record ProjectTemplate(string Id, string DisplayName, string Description, string EntryFile,
    TemplateBuild? Build = null, IReadOnlyDictionary<string, string>? Files = null);

/// <summary>模板叠加到器件的构建参数；HAL、SPL 和 RTOS 只启用所选组合。</summary>
public sealed record TemplateBuild(IReadOnlyList<string> Defines, IReadOnlyList<string> IncludeDirectories,
    IReadOnlyList<string> Sources, IReadOnlyList<string> CompileOptions, IReadOnlyList<string> LinkOptions);

public static class TemplateResolver
{
    public static DeviceDefinition Resolve(DeviceDefinition device, string templateId)
    {
        var template = device.Templates.Single(t => t.Id == templateId);
        if (template.Build is not { } build) return device;
        return device with
        {
            Defines = device.Defines.Concat(build.Defines).Distinct().ToArray(),
            IncludeDirectories = device.IncludeDirectories.Concat(build.IncludeDirectories).Distinct().ToArray(),
            Sources = device.Sources.Concat(build.Sources).Distinct().ToArray(),
            CompileOptions = device.CompileOptions.Concat(build.CompileOptions).Distinct().ToArray(),
            LinkOptions = device.LinkOptions.Concat(build.LinkOptions).Distinct().ToArray()
        };
    }
}
