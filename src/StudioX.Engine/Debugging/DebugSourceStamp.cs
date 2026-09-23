namespace StudioX.Engine.Debugging;

using System.Security.Cryptography;
using System.Text;

internal static class DebugSourceStamp
{
    // 不把构建日志、IDE 设置或 Git 元数据计入源码；覆盖工程内源文件、头文件和构建脚本。
    internal static async Task<string> ComputeAsync(string root, CancellationToken token)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo)
                {
                    if (entry.Name is not (".build" or ".git" or ".studiox" or "build" or "Build" or "Debug" or "Release")) pending.Push(entry.FullName);
                }
                else if (entry.Name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
                    new[] { ".c", ".h", ".s", ".cpp", ".cc", ".cxx", ".hpp", ".inc", ".ld", ".cmake", ".ioc" }.Contains(entry.Extension.ToLowerInvariant())) files.Add(entry.FullName);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // 编译设置影响二进制；保存或外部编辑后不能把旧 ELF 当作当前工程的调试映像。
        var settings = Path.Combine(root, ProjectBuildSettings.RelativePath);
        if (File.Exists(settings)) files.Add(settings);
        var stcIspPath = Path.Combine(root, StcIspSettings.RelativePath);
        StcIspSettings? stcIsp = File.Exists(stcIspPath) ? await StcIspSettings.ReadAsync(root, token) : null;
        if (stcIsp is { ClockMode: not StcClockMode.Preserve })
            hash.AppendData(Encoding.UTF8.GetBytes($"stc-clock\0{stcIsp.ClockMode}:{stcIsp.ClockFrequencyHz}\0"));
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/') + "\0"));
            await using var stream = File.OpenRead(file);
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
