namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>R89 的固定资格入口；芯片容量由器件包声明，工具身份不会回退到 8 位 SDCC。</summary>
public static class Mx251Profile
{
    public const string ToolsetId = "studiox.mx251";
    public const string Version = "0.1.89";
    public const string CompilerId = "mx251-r89";
    public const string ConfigurationFile = "mx251.json";
    public static string[] RequiredRoles => ["pwsh", "sdcc", "sdar", "sdas251", "sdld", "build251"];

    public static void ValidateTools(ResolvedToolset tools)
    {
        if (Mx251SdkProfile.Matches(tools.Manifest.Version, tools.Manifest.CompilerId))
        {
            Mx251SdkProfile.Validate(tools);
            return;
        }
        var manifest = tools.Manifest;
        if (manifest.Id != ToolsetId || manifest.Version != Version || manifest.CompilerId != CompilerId || manifest.Purpose != "mx251")
        {
            throw new StudioXException("MX251_IDENTITY", "需要明确的 MX251 R89 / 0.1.89 开发环境组件。");
        }
        var profile = tools.ResourceDirectory("profile");
        var compiler = tools.ResourceDirectory("sdcc");
        if (tools.Tool("build251") != PathBoundary.Resolve(profile, "Build-251.ps1") ||
            tools.Tool("sdcc") != PathBoundary.Resolve(compiler, "bin/sdcc.exe"))
        {
            throw new StudioXException("MX251_PROFILE", "组件入口与声明的 R89 资源目录不一致。");
        }
        foreach (var (path, expected) in new[]
        {
            (tools.Tool("sdcc"), "6b257de531614340c295ab1c12e080cec6e170f24942afa949cbacadbc172733"),
            (PathBoundary.Resolve(profile, "profile.lock.json"), "5bd1d39824c9cb85fd26a1b63e25081f9181f1ecb10cdb4d96c4acc0d72dd350")
        })
        {
            using var input = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_QUALIFICATION", "R89 编译器或资格锁已经改变，请使用匹配的组件，不能沿用 R89 验证结论。");
            }
        }
    }

    internal static async Task CreateProjectFilesAsync(InstalledPack pack, ProjectTemplate template, string root, CancellationToken token)
    {
        var relative = template.Mx251Configuration
            ?? throw new StudioXException("MX251_TEMPLATE", "器件模板缺少 MX251 构建配置。");
        var text = await File.ReadAllTextAsync(PathBoundary.Resolve(pack.RootDirectory, relative), token);
        using var config = JsonDocument.Parse(text);
        if (!config.RootElement.TryGetProperty("target", out var target) || target.GetString() != "AI8051U-34K64-32")
        {
            throw new StudioXException("MX251_TARGET", "当前资格范围仅为 AI8051U-34K64 的 32 位模式。");
        }
        await File.WriteAllTextAsync(PathBoundary.Resolve(root, ConfigurationFile), text, token);
        var cmake = PathBoundary.Resolve(pack.RootDirectory, "CMakeLists.txt");
        if (File.Exists(cmake))
        {
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), await File.ReadAllTextAsync(cmake, token), token);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "README.txt"),
            "MX251 R89 / AI8051U-34K64 32 位试用工程。F7 通过版本锁定组件编译，生成 HEX。\n" +
            "CMake工程在CMakeLists.txt登记源码、包含目录和宏；mx251.json保留内存模型、优化、ABI及运行库。旧JSON工程仍可编译。\n" +
            "普通调用使用 ordinary-wide + standard；数学运行库组合按组件 docs/使用说明.txt 选择，不能任意混换 ABI。\n" +
            "在工程详情选择 AI8H2K12G USB 下载器和IRC频率；下载固定32位并回读配置。Delay_MS跟随所设频率；不开放源码调试或通用GCC参数。\n" +
            "已验证 CODE 64KiB、物理 XDATA 32KiB，资格配置保守分配28KiB；EDATA 栈预留0x400–0x7ff。\n" +
            "公共bit ABI、任意ISR、未审计手写汇编和其它芯片不在全面验收范围。普通 C 编辑提示不验证目标 ABI。\n", token);
    }
}
