namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>ESP 链接器及核心导出文件在兼容的私有目录运行，完成后把构建记录复制回工程。</summary>
internal sealed class ArduinoBuildWorkspace : IDisposable
{
    private readonly string destination;
    private readonly string? staging;

    public string NativeDirectory
    {
        get;
    }

    public ArduinoBuildWorkspace(string data, string build, bool esp)
    {
        destination = build;
        Directory.CreateDirectory(build);
        if (!esp)
        {
            NativeDirectory = build;
            return;
        }
        staging = PathBoundary.Resolve(data, "arduino/build-stage/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        NativeDirectory = EspressifNativePath.For(staging);
    }

    public Task PublishAsync(CancellationToken token) => staging is null ? Task.CompletedTask : ArduinoRuntime.CopyAsync(staging, destination, token);

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
