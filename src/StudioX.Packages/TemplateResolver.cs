namespace StudioX.Packages;

public static class TemplateResolver
{
    public static DeviceDefinition Resolve(DeviceDefinition device, string templateId, bool enableAg32Logic = false)
    {
        var template = device.Templates.Single(t => t.Id == templateId);
        foreach (var build in new[] { template.Build, enableAg32Logic ? template.Ag32Sources?.Build : null }.OfType<TemplateBuild>())
        {
            device = device with
            {
                Defines = device.Defines.Concat(build.Defines).Distinct().ToArray(),
                IncludeDirectories = device.IncludeDirectories.Concat(build.IncludeDirectories).Distinct().ToArray(),
                Sources = device.Sources.Concat(build.Sources).Distinct().ToArray(),
                CompileOptions = device.CompileOptions.Concat(build.CompileOptions).Distinct().ToArray(),
                LinkOptions = device.LinkOptions.Concat(build.LinkOptions).Distinct().ToArray()
            };
        }
        return device;
    }
}
