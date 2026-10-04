namespace StudioX.Application.CodeIntelligence;

using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>只查看固定配置文件；小文件比较内容，大编译数据库比较元数据，避免每次轮询扫描 SDK。</summary>
internal static class AnalysisInputStamp
{
    public static string Capture(string runtime, string root, ProjectManifest project, IEnumerable<string>? additionalFiles = null)
    {
        var files = new[] { ".studiox/project.json", ".studiox/build.json", EspressifModuleSettings.RelativePath, "sdkconfig",
            ".build/compile_commands.json", ".build/CMakeCache.txt", ".build/studiox-idf-runtime.json",
            ".build/project_description.json", ".build/config/sdkconfig.json", ".build/config/sdkconfig.h", "device/manifest.json",
            ".build/toolchain/cflags", ".build/toolchain/cxxflags", ".build/toolchain/asmflags" }
            .Select(relative => PathBoundary.Resolve(root, relative))
            .Append(PathBoundary.Resolve(runtime, $"toolsets/{project.ToolsetId}/{project.ToolsetVersion}/toolset.json"))
            .Append(Path.Combine(runtime, "languages", "clangd", "bin", "clangd.exe"));
        var stamp = new StringBuilder();
        foreach (var path in files.Concat(additionalFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            stamp.Append(path).Append('|').Append(file.Exists ? file.Length : -1).Append('|').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
            if (file.Exists && file.Length <= 1024 * 1024)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stamp.Append('|').Append(Convert.ToHexString(SHA256.HashData(stream)));
            }
            stamp.AppendLine();
        }
        return stamp.ToString();
    }
}
