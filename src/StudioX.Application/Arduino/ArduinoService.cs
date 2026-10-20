namespace StudioX.Application.Arduino;

using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>协调 Arduino 工程、构建及 USB 上传所有权；界面不直接执行 CLI 或持有串口。</summary>
public sealed class ArduinoService(BuildService builds, DeviceHub devices,
    Func<string, CancellationToken, Task>? initializeRepository = null)
{
    public IReadOnlyList<ArduinoBoard> Boards => ArduinoBoard.Supported;

    public Task<ProjectManifest> CreateAsync(string name, string boardId, string destination, string? importDirectory = null,
        CancellationToken token = default) => new ArduinoProjectService(initializeRepository)
            .CreateAsync(name, boardId, destination, importDirectory, token);

    public Task<ProjectManifest> CreateFromPackAsync(InstalledPack pack, string deviceId, string boardId, string name,
        string destination, CancellationToken token = default) => new ArduinoProjectService(initializeRepository)
            .CreateFromPackAsync(pack, deviceId, boardId, name, destination, token);

    public Task<ArduinoLibrary[]> InstallLibraryAsync(string root, string name, string version, IProgress<string>? output = null,
        CancellationToken token = default) => builds.Arduino.InstallLibraryAsync(root, new(name, version), output, token);

    public Task RestoreLibrariesAsync(string root, IProgress<string>? output = null, CancellationToken token = default) =>
        builds.Arduino.RestoreLibrariesAsync(root, output, token);

    public Task<ArduinoLibrarySearchResult[]> SearchLibrariesAsync(string root, string query, CancellationToken token = default) =>
        builds.Arduino.SearchLibrariesAsync(root, query, token);

    public Task<ArduinoUploadPreview> PreviewUploadAsync(string root, CancellationToken token = default) => builds.Arduino.PreviewAsync(root, token);

    public async Task<ProcessResult> UploadApprovedAsync(string root, string port, string approvedIdentity,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        port = ArduinoBuildService.ValidatePort(port);
        using var ownership = await devices.ReserveAsync("serial:" + port, token);
        return await builds.Arduino.UploadApprovedAsync(root, port, approvedIdentity, output, token);
    }
}
