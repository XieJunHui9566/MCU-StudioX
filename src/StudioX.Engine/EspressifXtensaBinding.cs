namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>避开厂商目标启动器对 SO 文件取 8.3 别名；仍使用同组件 GCC、精确芯片配置和对应多库。</summary>
internal sealed record EspressifXtensaBinding(string Gcc, string Gxx, string ConfigFile, string ConfigName)
{
    internal static ResolvedToolset Compilers(ResolvedToolset tools, EspressifProjectSettings sdk)
    {
        var target = tools.ForEspressifTarget(sdk.Target);
        var binding = Create(tools, sdk);
        if (binding is null)
        {
            return target;
        }
        var executables = new Dictionary<string, string>(target.Manifest.Executables, StringComparer.Ordinal)
        {
            ["gcc"] = Path.GetRelativePath(tools.RootDirectory, binding.Gcc).Replace('\\', '/'),
            ["gxx"] = Path.GetRelativePath(tools.RootDirectory, binding.Gxx).Replace('\\', '/')
        };
        return target with
        {
            Manifest = target.Manifest with
            {
                Executables = executables
            }
        };
    }
    internal static EspressifXtensaBinding? Create(ResolvedToolset tools, EspressifProjectSettings sdk)
    {
        if (!OperatingSystem.IsWindows() || sdk.Framework != "esp-idf" || sdk.Target is not ("esp32" or "esp32s3"))
        {
            return null;
        }
        var target = tools.ForEspressifTarget(sdk.Target);
        var bin = Path.GetDirectoryName(target.Tool("gcc"))!;
        var gcc = Path.Combine(bin, "xtensa-esp-elf-gcc.exe");
        var gxx = Path.Combine(bin, "xtensa-esp-elf-g++.exe");
        var configName = "xtensa_" + sdk.Target + ".so";
        var config = Path.Combine(Path.GetDirectoryName(bin)!, "lib", configName);
        foreach (var file in new[] { gcc, gxx, config })
        {
            var relative = Path.GetRelativePath(tools.RootDirectory, file).Replace('\\', '/');
            if (!File.Exists(file) || !tools.Manifest.Sha256.ContainsKey(relative))
            {
                throw new StudioXException("ESPRESSIF_XTENSA_BINDING", "缺少受索引的 Xtensa 动态配置工具：" + relative);
            }
        }
        return new(gcc, gxx, config, configName);
    }
}
