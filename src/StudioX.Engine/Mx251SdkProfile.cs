namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>按 SDK 构建契约接入 MX251；组件锁由工程选择，IDE 不维护编译器版本或二进制白名单。</summary>
public static partial class Mx251SdkProfile
{
    [GeneratedRegex(@"^mx251-v[0-9]+\.[0-9]{4}(?:LST)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerIdentity();
    // 此处仅路由到 SDK 后端；实际格式、入口和内容必须经 Validate 校验。
    public static bool Matches(string version, string compilerId) => !string.IsNullOrWhiteSpace(version) && CompilerIdentity().IsMatch(compilerId);
    public static bool Matches(ProjectManifest project) => project.ToolsetId == Mx251Profile.ToolsetId && Matches(project.ToolsetVersion, project.CompilerId);
    public static string Target(string deviceId) => deviceId switch
    {
        "AI8051U-34K64" => "AI8051U-34K64-32",
        "STC32G12K128" => "STC32G12K128",
        _ => throw new StudioXException("MX251_TARGET", "当前适配没有该器件的明确目标映射。")
    };
    private static string Root(ResolvedToolset tools) => tools.ResourceDirectory("mx251-sdk");
    internal static string Profile(ResolvedToolset tools, string deviceId)
    {
        using var sdk = Open(tools);
        var target = Target(deviceId);
        if (!sdk.RootElement.GetProperty("targets").TryGetProperty(target, out var declaration))
        {
            throw new StudioXException("MX251_TARGET", "所选 SDK 未声明工程目标：" + target);
        }
        return PathBoundary.Resolve(Root(tools), declaration.GetProperty("profile").GetString()!);
    }
    internal static string Module(ResolvedToolset tools)
    {
        using var sdk = Open(tools);
        return PathBoundary.Resolve(Root(tools), sdk.RootElement.GetProperty("projectModule").GetString()!);
    }
    private static JsonDocument Open(ResolvedToolset tools)
    {
        var file = PathBoundary.Resolve(Root(tools), "sdk.json");
        if (!File.Exists(file))
        {
            throw new StudioXException("MX251_SDK", "组件缺少独立 SDK 清单 sdk.json。");
        }
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(file));
        }
        catch (JsonException error) { throw new StudioXException("MX251_SDK", "SDK 清单不是有效 JSON。", error); }
    }
    internal static void Validate(ResolvedToolset tools)
    {
        if (tools.Manifest.Id != Mx251Profile.ToolsetId || tools.Manifest.Purpose != "mx251" || !Matches(tools.Manifest.Version, tools.Manifest.CompilerId))
        {
            throw new StudioXException("MX251_IDENTITY", "需要明确的 MX251 SDK 组件身份。");
        }
        try
        {
            ValidateContract(tools);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new StudioXException("MX251_SDK", "SDK 清单字段不完整或格式不兼容。", error);
        }
    }
    private static void ValidateContract(ResolvedToolset tools)
    {
        using var document = Open(tools);
        var sdk = document.RootElement;
        UniqueProperties(sdk);
        var root = Root(tools);
        if (sdk.GetProperty("schema").GetInt32() != 1 || sdk.GetProperty("name").GetString() != "MX251" ||
            sdk.GetProperty("host").GetString() != tools.Manifest.Host ||
            "mx251-v" + sdk.GetProperty("version").GetString() != tools.Manifest.CompilerId ||
            !sdk.GetProperty("buildChain").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "CMake", "Ninja", "MX251" }))
        {
            throw new StudioXException("MX251_SDK", "SDK 格式、宿主、编译器身份或构建契约与组件不一致。");
        }
        var files = sdk.GetProperty("files");
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ComponentPath(string path) => Path.GetRelativePath(tools.RootDirectory, path).Replace('\\', '/');
        string DeclaredFile(string relative)
        {
            var path = PathBoundary.Resolve(root, relative);
            if (!files.TryGetProperty(relative, out var value) || value.GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
                !tools.Manifest.Sha256.TryGetValue(ComponentPath(path), out var outer) || !hash.Equals(outer, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_SDK", "SDK 文件未一致地纳入组件内容锁：" + relative);
            }
            return path;
        }
        void HashFile(string path, string expected)
        {
            using var input = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_SDK", "SDK 文件内容与声明不匹配：" + Path.GetRelativePath(root, path));
            }
        }
        if (!tools.Manifest.Sha256.ContainsKey(ComponentPath(PathBoundary.Resolve(root, "sdk.json"))))
        {
            throw new StudioXException("MX251_SDK", "SDK 清单没有纳入组件内容锁。");
        }
        foreach (var entry in files.EnumerateObject())
        {
            if (!indexed.Add(entry.Name))
            {
                throw new StudioXException("MX251_SDK", "SDK 存在重复文件索引：" + entry.Name);
            }
            DeclaredFile(entry.Name);
        }
        var prefix = ComponentPath(root) + "/";
        if (tools.Manifest.Sha256.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..]).Any(p => p != "sdk.json" && !indexed.Contains(p)))
        {
            throw new StudioXException("MX251_SDK", "组件在 SDK 内包含未被 SDK 清单索引的文件。");
        }
        var script = DeclaredFile(sdk.GetProperty("buildScript").GetString()!);
        DeclaredFile(sdk.GetProperty("projectModule").GetString()!);
        if (!string.Equals(script, tools.Tool("build251"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(PathBoundary.Resolve(root, "toolchain"), tools.ResourceDirectory("sdcc"), StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("MX251_SDK", "IDE 组件入口与独立 SDK 的声明不一致。");
        }
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var compilerDeclared = false;
        foreach (var target in sdk.GetProperty("targets").EnumerateObject())
        {
            if (!targets.Add(target.Name))
            {
                throw new StudioXException("MX251_SDK", "SDK 存在重复目标。");
            }
            var relative = target.Value.GetProperty("profile").GetString()!;
            var profile = PathBoundary.Resolve(root, relative);
            var lockFile = DeclaredFile(relative + "/profile.lock.json");
            HashFile(lockFile, target.Value.GetProperty("profileLockSha256").GetString()!);
            using var profileDocument = JsonDocument.Parse(File.ReadAllBytes(lockFile));
            var identity = profileDocument.RootElement;
            if (identity.GetProperty("schema").GetInt32() != 2 || identity.GetProperty("target").GetString() != target.Name ||
                identity.GetProperty("mx251Version").GetString() != sdk.GetProperty("version").GetString() ||
                !string.Equals(profile, tools.ResourceDirectory("profile-" + target.Name), StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_SDK", "目标配置的身份与 SDK 不一致：" + target.Name);
            }
            var overlay = identity.GetProperty("compilerOverlay");
            var compiler = DeclaredFile(relative + "/" + overlay.GetProperty("path").GetString());
            var hash = target.Value.GetProperty("compilerSha256").GetString()!;
            if (!hash.Equals(overlay.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_SDK", "编译器锁与 SDK 目标声明不一致。");
            }
            HashFile(compiler, hash);
            compilerDeclared |= string.Equals(compiler, tools.Tool("sdcc"), StringComparison.OrdinalIgnoreCase);
        }
        if (targets.Count == 0 || !compilerDeclared)
        {
            throw new StudioXException("MX251_SDK", "SDK 没有声明有效目标和编译器入口。");
        }
        // 完整文件内容由 ToolsetCatalog 校验；目标配置、运行库、ABI 和输入快照仍由同一 SDK 入口复核。
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name))
                {
                    throw new StudioXException("MX251_SDK", "SDK 清单包含重复字段：" + p.Name);
                }
                UniqueProperties(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                UniqueProperties(item);
            }
        }
    }
}
