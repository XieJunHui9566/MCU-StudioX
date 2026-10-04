namespace StudioX.SecurityValidation;

using StudioX.Foundation;

/// <summary>路径策略在文件 I/O 前拒绝 Windows 设备别名及平台相关的歧义名称。</summary>
internal static class PathChecks
{
    internal static Task RunAsync(string root)
    {
        foreach (var relative in new[] { "COM¹.c", "nested/com².hpp", "COM³", "LPT¹.c", "lpt².h", "nested/LPT³.txt",
            "COM1.c", "CON", "../escape.c", "source.c:secret", "source.c.", "source.c " })
        {
            try
            {
                _ = PathBoundary.Resolve(root, relative);
            }
            catch (StudioXException error) when (error.Code == "PATH_UNSAFE")
            {
                Console.WriteLine("PASS unsafe path rejected before I/O: " + relative);
                continue;
            }
            throw new InvalidOperationException("Unsafe path accepted: " + relative);
        }
        foreach (var relative in new[] { "COM10.c", "composite.c", "lpt-driver.h", "中文/main.c" })
        {
            _ = PathBoundary.Resolve(root, relative);
            Console.WriteLine("PASS ordinary path accepted: " + relative);
        }
        return Task.CompletedTask;
    }
}
