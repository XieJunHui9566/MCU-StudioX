namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>ESP 链接器及核心导出文件在兼容的私有目录运行，完成后把构建记录复制回工程。</summary>
internal sealed class ArduinoBuildWorkspace : IDisposable
{
    private readonly string destination;
    private readonly string? staging;
    private readonly string? artifactDestination;
    public string? NativeArtifactsDirectory
    {
        get;
    }

    public string NativeDirectory
    {
        get;
    }

    public ArduinoBuildWorkspace(string data, string build, bool esp, string? artifacts = null)
    {
        destination = build;
        artifactDestination = artifacts;
        Directory.CreateDirectory(build);
        if (!esp && build.All(c => c < 127))
        {
            NativeDirectory = build;
            NativeArtifactsDirectory = artifacts;
            return;
        }
        staging = PathBoundary.Resolve(data, "arduino/build-stage/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "build"));
        NativeDirectory = EspressifNativePath.For(Path.Combine(staging, "build"));
        if (artifacts is not null)
        {
            Directory.CreateDirectory(Path.Combine(staging, "output"));
            NativeArtifactsDirectory = EspressifNativePath.For(Path.Combine(staging, "output"));
        }
    }

    public async Task PublishAsync(CancellationToken token)
    {
        if (staging is null)
        {
            return;
        }
        await ArduinoRuntime.CopyAsync(NativeDirectory, destination, token);
        if (artifactDestination is not null)
        {
            await ArduinoRuntime.CopyAsync(NativeArtifactsDirectory!, artifactDestination, token);
        }
    }

    public void Dispose()
    {
        if (staging is null || !Directory.Exists(staging))
        {
            return;
        }
        // 删除前重新检查整棵本轮临时树的链接边界；正式工程和用户工具始终在此目录之外。
        _ = ToolsetSnapshot.Capture(staging, CancellationToken.None);
        Directory.Delete(staging, true);
    }
}
