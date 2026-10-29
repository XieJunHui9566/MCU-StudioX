namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private readonly Dictionary<string, string[]> mx251SourceDefines = new(StringComparer.OrdinalIgnoreCase);

    private async Task ReadMx251CMakeAnalysisAsync(CancellationToken token)
    {
        var path = PathBoundary.Resolve(projectRoot, ".build/mx251-cmake/mx251-inputs.json");
        if (!File.Exists(path))
        {
            return;
        }
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw new StudioXException("MX251_ANALYSIS", "CMake输入清单过大，请重新配置。");
        }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        var root = json.RootElement;
        var includes = root.GetProperty("includeDirectories").EnumerateArray().Select(x =>
            "-I" + PathBoundary.Resolve(projectRoot, Path.GetRelativePath(projectRoot, x.GetString()!)));
        flags = [.. flags, .. includes, .. root.GetProperty("defines").EnumerateArray().Select(x => "-D" + x.GetString())];
        foreach (var source in root.GetProperty("sources").EnumerateArray())
        {
            if (source.TryGetProperty("defines", out var defines))
            {
                mx251SourceDefines[Path.GetFullPath(source.GetProperty("file").GetString()!)] = defines.EnumerateArray().Select(x => "-D" + x.GetString()).ToArray();
            }
        }
    }
}
