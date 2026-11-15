namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>CLI 使用 ASCII sketch 副本；显示名称、原始文件、内容锁与上传凭据仍属于用户工程。</summary>
internal sealed class ArduinoSketchWorkspace : IDisposable
{
    private readonly string root;
    private readonly string? stage;
    private readonly string entry;
    internal string Directory
    {
        get;
    }
    internal string Identifier
    {
        get;
    }

    private ArduinoSketchWorkspace(string root, string identifier, string entry, string? stage, string directory)
    {
        this.root = Path.GetFullPath(root);
        this.stage = stage;
        this.entry = entry;
        Identifier = identifier;
        Directory = directory;
    }

    internal static async Task<ArduinoSketchWorkspace> CreateAsync(string data, string root, ProjectManifest project, CancellationToken token)
    {
        var identifier = ArduinoProjectSettings.SketchIdentifier(project.Name);
        if (identifier == project.Name && Path.GetFullPath(root).All(c => c < 127 && c > ' '))
        {
            return new(root, identifier, project.EntryFile!, null, Path.GetFullPath(root));
        }
        var stage = PathBoundary.Resolve(data, "arduino/source-stage/" + Guid.NewGuid().ToString("N"));
        var directory = PathBoundary.Resolve(stage, identifier);
        System.IO.Directory.CreateDirectory(directory);
        var workspace = new ArduinoSketchWorkspace(root, identifier, project.EntryFile!, stage, EspressifNativePath.For(directory));
        try
        {
            var before = await ArduinoSourceSnapshot.HashAsync(root, true, token);
            foreach (var relative in ArduinoSourceSnapshot.Files(root))
            {
                token.ThrowIfCancellationRequested();
                var renamed = relative == project.EntryFile ? identifier + ".ino" : relative;
                var target = PathBoundary.Resolve(directory, renamed);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // CreateNew 防止主 sketch 改名时覆盖同名的辅助文件。
                await using var input = File.OpenRead(PathBoundary.Resolve(root, relative));
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                await input.CopyToAsync(output, token);
            }
            if (before != await ArduinoSourceSnapshot.HashAsync(root, true, token))
            {
                throw new StudioXException("ARDUINO_BUILD_CHANGED", "建立 sketch 副本期间工程发生变化，请重新编译。");
            }
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    internal string Display(string value)
    {
        if (stage is null)
        {
            return value;
        }
        foreach (var directory in new[] { Directory, Path.Combine(stage, Identifier) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            value = value.Replace(Path.Combine(directory, Identifier + ".ino"), Path.Combine(root, entry), StringComparison.OrdinalIgnoreCase)
                .Replace((directory + "/" + Identifier + ".ino").Replace('\\', '/'), Path.Combine(root, entry).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                .Replace(directory.Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                .Replace(directory, root, StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }

    public void Dispose()
    {
        if (stage is null || !System.IO.Directory.Exists(stage))
        {
            return;
        }
        _ = ToolsetSnapshot.Capture(stage, CancellationToken.None);
        System.IO.Directory.Delete(stage, true);
    }
}
