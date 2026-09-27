namespace StudioX.Packages;

public static class TemplateResolver
{
    public static DeviceDefinition Resolve(DeviceDefinition device, string templateId)
    {
        var template = device.Templates.Single(t => t.Id == templateId);
        if (template.Build is not { } build)
        {
            return device;
        }
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
