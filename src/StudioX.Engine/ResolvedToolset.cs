namespace StudioX.Engine;

using StudioX.Foundation;
public sealed record ResolvedToolset(ToolsetManifest Manifest, string RootDirectory, string Fingerprint)
{
    public string Tool(string role) => PathBoundary.Resolve(RootDirectory, Manifest.Executables.TryGetValue(role, out var path)
        ? path : throw new StudioXException("TOOL_ROLE", $"开发环境组件缺少组件：{role}"));
    public string ResourceDirectory(string role) => PathBoundary.Resolve(RootDirectory, Manifest.ResourceDirectories is not null && Manifest.ResourceDirectories.TryGetValue(role, out var path)
        ? path : throw new StudioXException("TOOL_RESOURCE", $"开发环境组件缺少资源目录：{role}"));

    /// <summary>共享 SDK 保留一份；通用分析工具按工程的明确目标选择对应编译器。</summary>
    public ResolvedToolset ForEspressifTarget(string target)
    {
        var suffix = target switch
        {
            "esp32" => "esp32",
            "esp32s3" => "esp32s3",
            "esp32c3" or "esp32c5" or "esp32c6" or "esp32p4" => "riscv",
            "esp8266" => "esp8266",
            _ => throw new StudioXException("ESPRESSIF_TARGET", "不支持的 Espressif 目标：" + target)
        };
        var executables = new Dictionary<string, string>(Manifest.Executables, StringComparer.Ordinal);
        foreach (var role in new[] { "gcc", "gxx", "ar", "ranlib", "objcopy", "objdump", "size", "gdb", "readelf" })
        {
            if (executables.TryGetValue(role + "-" + suffix, out var relative))
            {
                executables[role] = relative;
            }
        }
        if (!executables.ContainsKey("gcc") || !executables.ContainsKey("gxx"))
        {
            throw new StudioXException("TOOL_ROLE", "开发环境组件缺少目标 " + target + " 的 C/C++ 编译器。");
        }
        return this with
        {
            Manifest = Manifest with
            {
                Executables = executables
            }
        };
    }
}
