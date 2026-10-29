namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>CMake 只管理工程输入，目标编译、ABI 和发布检查仍走 MX251 专用流程。</summary>
internal static class Mx251CMake
{
    internal const string ToolsetId = "studiox.cmake";
    internal const string Version = "4.4.0";
    internal static bool Enabled(string root) => File.Exists(PathBoundary.Resolve(root, "CMakeLists.txt"));

    internal static async Task<string> WriteSupportAsync(string root, CancellationToken token)
    {
        var directory = PathBoundary.Resolve(root, ".build/mx251-support");
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "Build.ps1", "Project.cmake" })
        {
            using var stream = typeof(Mx251CMake).Assembly.GetManifestResourceStream("StudioX.Engine.Mx251." + name)
                ?? throw new InvalidOperationException("Missing MX251 build resource: " + name);
            using var reader = new StreamReader(stream);
            await File.WriteAllTextAsync(Path.Combine(directory, name), await reader.ReadToEndAsync(token), token);
        }
        return directory;
    }

    internal static ProcessRequest Configure(string root, string support, ResolvedToolset compiler,
        ResolvedToolset buildTools, Dictionary<string, string> environment, IProgress<string>? output, ProjectManifest project)
    {
        var qualified = Mx251SdkProfile.Matches(project);
        var arguments = new List<string> { "-S", root, "-B", PathBoundary.Resolve(root, ".build/mx251-cmake"), "-G", "Ninja",
            "-DCMAKE_MAKE_PROGRAM=" + buildTools.Tool("ninja"),
            "-DMX251_CMAKE_MODULE=" + Path.Combine(support, "Project.cmake"),
            "-DMX251_BUILD_SCRIPT=" + (qualified ? compiler.Tool("build251") : Path.Combine(support, "Build.ps1")),
            "-DMX251_PWSH=" + compiler.Tool("pwsh"),
            "-DMX251_PROFILE=" + (qualified ? Mx251SdkProfile.Profile(compiler, project.DeviceId) : compiler.ResourceDirectory("profile")),
            "-DMX251_TOOLCHAIN=" + compiler.ResourceDirectory("sdcc") };
        if (qualified)
        {
            arguments.Add("-DMX251_PROJECT_MODULE=" + Mx251SdkProfile.Module(compiler));
            // 工程可显式 set/option 开启；删除工程设置后不能悄悄继承旧缓存的 ON。
            arguments.Add("-UMX251_LEAF_CALL_FEEDBACK");
        }
        return new(buildTools.Tool("cmake"), arguments, root, TimeSpan.FromMinutes(2), environment,
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output, ContainProcessTree: qualified);
    }
}
