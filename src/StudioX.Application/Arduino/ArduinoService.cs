namespace StudioX.Application.Arduino;

using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>协调 Arduino 工程、构建及 USB 上传所有权；界面不直接执行 CLI 或持有串口。</summary>
public sealed class ArduinoService(BuildService builds, DeviceHub devices,
    Func<string, CancellationToken, Task>? initializeRepository = null, Func<Task<string[]>>? listPorts = null)
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

    public Task<string[]> ListPortsAsync() => listPorts?.Invoke() ?? Task.Run(SerialTransport.GetPortNames);

    public Task<ProjectManifest> ChangeBoardAsync(string root, ProjectManifest expected, string boardId, CancellationToken token = default) =>
        builds.Arduino.ChangeBoardAsync(root, expected, boardId, token);

    public async Task<ArduinoUploadSettings> LoadUploadSettingsAsync(string root, CancellationToken token = default)
    {
        await RequireProjectAsync(root, token);
        var path = PathBoundary.Resolve(root, ArduinoUploadSettings.RelativePath);
        var settings = File.Exists(path) ? await JsonStore.ReadAsync<ArduinoUploadSettings>(path, token) : new();
        settings.Validate();
        return settings;
    }

    public async Task SaveUploadSettingsAsync(string root, string port, CancellationToken token = default)
    {
        await RequireProjectAsync(root, token);
        var settings = new ArduinoUploadSettings(Port: ArduinoBuildService.ValidatePort(port));
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ArduinoUploadSettings.RelativePath), settings, token);
    }

    private static async Task RequireProjectAsync(string root, CancellationToken token)
    {
        if ((await ProjectService.ReadAsync(root, token)).Kind != ProjectKind.Arduino)
        {
            throw new StudioXException("ARDUINO_PROJECT", "此操作只适用于 Arduino 工程。");
        }
    }

    public async Task<ArduinoUploadReport> UploadApprovedAsync(string root, string port, string approvedIdentity,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        port = ArduinoBuildService.ValidatePort(port);
        using var ownership = await devices.ReserveAsync("serial:" + port, token);
        if (!(await ListPortsAsync()).Contains(port, StringComparer.OrdinalIgnoreCase))
        {
            throw new StudioXException("ARDUINO_PORT_UNAVAILABLE", $"串口 {port} 当前不存在；请连接开发板并刷新串口列表，重新确认端口。");
        }
        token.ThrowIfCancellationRequested();
        return await builds.Arduino.UploadApprovedAsync(root, port, approvedIdentity, output, token);
    }
}
