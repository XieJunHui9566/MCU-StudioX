namespace StudioX.PortableDesktop;

/// <summary>启动时明确选择工程、运行组件和独立用户数据；不会发现或猜测系统工具。</summary>
internal sealed record StartupOptions(string? ProjectDirectory, string RuntimeDirectory, string DataDirectory, string? ValidationDirectory)
{
    public static StartupOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var key = arguments[index];
            if (key is not ("--project" or "--runtime" or "--data" or "--validate-workflow") || index + 1 >= arguments.Length || !values.TryAdd(key, arguments[index + 1]))
            {
                throw new ArgumentException("参数格式：--project <工程目录> --runtime <内置组件目录> --data <用户数据目录>");
            }
        }
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new(values.GetValueOrDefault("--project"),
            Path.GetFullPath(values.GetValueOrDefault("--runtime") ?? Path.Combine(AppContext.BaseDirectory, "runtime")),
            Path.GetFullPath(values.GetValueOrDefault("--data") ?? Path.Combine(localData, "MCU-StudioX", "linux-preview")),
            values.GetValueOrDefault("--validate-workflow"));
    }
}
