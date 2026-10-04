using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;

internal static class EspressifNativeToolChecks
{
    internal static async Task<int> RunAsync(ToolsetCatalog catalog, string source, string output)
    {
        var root = Path.Combine(output, "project with spaces");
        if (Directory.Exists(root))
        {
            throw new ArgumentException("Use a new native-path validation directory.");
        }
        var checks = new List<string>();
        var manifest = await ProjectService.ReadAsync(source);
        if (manifest.Espressif is not { Framework: "esp-idf", Target: "esp32s3" })
        {
            throw new ArgumentException("Use the bound ESP32-S3 ESP-IDF project.");
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] is ".build" or ".git")
            {
                continue;
            }
            var destination = PathBoundary.Resolve(root, relative.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        Directory.CreateDirectory(Path.Combine(root, "include"));
        var tools = (await catalog.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId)).ForEspressifTarget("esp32s3");
        var initialized = await new ProcessRunner().RunAsync(new(tools.Tool("git"),
            ["-c", "init.templateDir=", "init", "--initial-branch=main", "."], root, TimeSpan.FromSeconds(30),
            ToolsetEnvironment.Create(tools), RemoveEnvironment: GitRepositoryService.AmbientVariables));
        Check(initialized.Success, "validation project has an independent unborn Git repository: " + initialized.StandardOutput.Trim() + initialized.StandardError.Trim());
        var protectedFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).StartsWith(".studiox" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => path, Hash);
        var previous = Environment.GetEnvironmentVariable("XTENSA_GNU_CONFIG");
        try
        {
            // 仅污染本次测试进程；不得改用户或系统环境，也不能让另一目标的动态配置泄漏进 IDF。
            Environment.SetEnvironmentVariable("XTENSA_GNU_CONFIG", "intentional-mismatched-xtensa-config.so");
            var builds = new BuildService(catalog);
            var report = await builds.BuildAsync(root, new ProgressText());
            await File.WriteAllTextAsync(Path.Combine(output, "native-build.log"), report.Log);
            Check(report.Success && report.ExitCode == 0, "space-containing project and installed SDK build successfully with contaminated ambient Xtensa variable");
            Check(!report.Log.Contains("pointed different files", StringComparison.Ordinal), "compiler dynamic configuration conflict is absent");
            Check(report.Log.Contains("Git repository has no commits; skipping revision lookup", StringComparison.Ordinal), "unborn repository uses the IDF default without creating a commit");
            Check(await ProjectVersionAsync() == "1", "unborn repository firmware version matches the SDK default");
            foreach (var subproject in new[] { ".build", ".build/bootloader" })
            {
                var cache = await File.ReadAllLinesAsync(PathBoundary.Resolve(root, subproject + "/CMakeCache.txt"));
                foreach (var (language, role) in new[] { ("C", "gcc"), ("CXX", "gxx"), ("ASM", "gcc") })
                {
                    var prefix = "CMAKE_" + language + "_COMPILER:FILEPATH=";
                    var path = cache.Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
                    Check(Path.GetFileName(path) == Path.GetFileName(tools.Tool(role)) && !path.Any(char.IsWhiteSpace), subproject + " " + language + " compiler preserves full executable basename without spaces");
                    Check(EspressifPathIdentity.NormalizePath(path).Equals(EspressifPathIdentity.NormalizePath(tools.Tool(role)), StringComparison.OrdinalIgnoreCase), subproject + " " + language + " compiler is the locked target file");
                }
                var rules = await File.ReadAllTextAsync(PathBoundary.Resolve(root, subproject + "/CMakeFiles/rules.ninja"));
                Check(rules.Contains("xtensa-esp32s3-elf-gcc.exe", StringComparison.Ordinal), subproject + " Ninja commands retain target-specific compiler filename");
            }
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".build/studiox-build-receipt.json")));
            Check(receipt.RootElement.GetProperty("images").GetArrayLength() >= 3, "application, partition table and bootloader artifacts have a validated build receipt");
            foreach (var (path, hash) in protectedFiles)
            {
                Check(Hash(path) == hash, "preserve input " + Path.GetRelativePath(root, path));
            }
            var incremental = await builds.BuildAsync(root, new ProgressText());
            await File.WriteAllTextAsync(Path.Combine(output, "incremental-build.log"), incremental.Log);
            Check(incremental.Success && !incremental.Log.Contains("构建环境已变化", StringComparison.Ordinal), "subsequent build reuses native cache without compiler-change reset");
            var cmake = Path.Combine(root, "CMakeLists.txt");
            var originalCmake = await File.ReadAllTextAsync(cmake);
            try
            {
                await File.WriteAllTextAsync(cmake, "set(PROJECT_VER \"4.5.6\")\n" + originalCmake);
                await CheckVersionAsync("4.5.6", "explicit PROJECT_VER");
                await File.WriteAllTextAsync(cmake, originalCmake);
                await File.WriteAllTextAsync(Path.Combine(root, "version.txt"), "5.6.7\n");
                await CheckVersionAsync("5.6.7", "version.txt");
                File.Delete(Path.Combine(root, "version.txt"));
                var versionCmake = Regex.Replace(originalCmake, @"(?m)^project\(([^\r\n()]*)\)", "project($1 VERSION 6.7.8)");
                if (versionCmake == originalCmake)
                {
                    throw new InvalidOperationException("Version validation needs a plain project() declaration.");
                }
                await File.WriteAllTextAsync(cmake, versionCmake);
                await CheckVersionAsync("6.7.8", "project(VERSION)");
            }
            finally
            {
                await File.WriteAllTextAsync(cmake, originalCmake);
                File.Delete(Path.Combine(root, "version.txt"));
            }
            _ = await catalog.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId, forceVerification: true);
            Check(true, "SDK and all toolchain files still match the original hash manifest after build");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                status = "passed",
                count = checks.Count,
                checks,
                hardwareConnected = false
            }, JsonStore.Options));
            Console.WriteLine("PASS " + checks.Count + " native path and build checks.");
            return 0;
        }
        finally { Environment.SetEnvironmentVariable("XTENSA_GNU_CONFIG", previous); }

        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        async Task<string> ProjectVersionAsync()
        {
            using var description = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".build/project_description.json")));
            return description.RootElement.GetProperty("project_version").GetString()!;
        }
        async Task CheckVersionAsync(string expected, string label)
        {
            var configured = await new BuildService(catalog).ConfigureAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, "version-" + expected + ".log"), configured.Log);
            Check(configured.Success && await ProjectVersionAsync() == expected, label + " retains the user's firmware version in an unborn repository");
        }
    }
}
