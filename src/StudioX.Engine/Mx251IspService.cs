namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Ai8051Isp;
using StudioX.Foundation;
using StudioX.Packages;
using IspImage = StudioX.Ai8051Isp.FirmwareImage;

/// <summary>MX251 固件的离线准备、快照复核和固定 32 位 ISP；不沿用 8 位 STC 的零基址 HEX 流程。</summary>
public sealed class Mx251IspService(ToolsetCatalog catalog, string dataDirectory, IMx251IspBackend? backend = null)
{
    private readonly IMx251IspBackend backend = backend ?? new Mx251HidBackend();
    private readonly SemaphoreSlim gate = new(1, 1);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static StudioXException Invalid(string message) => new("MX251_ISP", message);
    private string SettingsPath(string root) => Path.Combine(dataDirectory, "mx251-isp", Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())) + ".json");

    public Task<IReadOnlyList<HidDevice>> ListDevicesAsync(CancellationToken token = default) => Task.Run(backend.Enumerate, token);
    public async Task<Mx251IspSettings> LoadSettingsAsync(string root, CancellationToken token = default)
    {
        await ValidateProjectAsync(root, token);
        var path = SettingsPath(root);
        var settings = File.Exists(path) ? await JsonStore.ReadAsync<Mx251IspSettings>(path, token) : new();
        // 旧本机设置仅供首次迁移；工程一旦声明时钟，所有电脑都以工程值为准。
        if (await Mx251ClockConfiguration.ReadAsync(root, token) is { } hz)
        {
            settings = settings with
            {
                IrcHz = hz
            };
        }
        settings.Validate();
        return settings;
    }
    public async Task SaveSettingsAsync(string root, Mx251IspSettings settings, CancellationToken token = default)
    {
        using var operation = Mx251BuildStorage.Acquire(root);
        await ValidateProjectAsync(root, token);
        // 保存时不触碰设备，允许板未连接时设置频率；下载时仍严格核对设备身份。
        settings.Validate();
        await JsonStore.WriteAsync(SettingsPath(root), settings, token);
        await Mx251ClockConfiguration.SaveAsync(root, settings.IrcHz, token);
    }
    private static HidDevice SelectDevice(Mx251IspSettings settings, IReadOnlyList<HidDevice> devices)
    {
        settings.Validate(requireDevice: true);
        var matches = devices.Where(d => d.Path.Equals(settings.DevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw Invalid("所选 USB 下载器未连接或身份不唯一，请刷新并明确选择；不会自动切换到另一块板。");
        }
        var device = matches[0];
        if (device.VendorId != 0x34bf || device.ProductId != 0xff0f || device.InputBytes != 65 || device.OutputBytes != 65 ||
            !device.Path.Contains("vid_34bf&pid_ff0f&mi_00", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("下载器不在已验证的 AI8H2K12G / 34BF:FF0F / MI_00 白名单内。");
        }
        return device;
    }
    private static async Task<ProjectManifest> ValidateProjectAsync(string root, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        if (project.Kind != ProjectKind.Pack || project.ToolsetId != Mx251Profile.ToolsetId || project.CompilerId != Mx251Profile.CompilerId ||
            project.ToolsetVersion != Mx251Profile.Version || project.DeviceId != "AI8051U-34K64" ||
            project.PackId is not ("stc.stc251" or "stc.ai8051u-mx251"))
        {
            throw Invalid("仅支持 STC251 器件包中的 AI8051U-34K64 32 位 MX251 工程。");
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(d => d.Id == project.DeviceId);
        if (pack.Id != project.PackId || pack.Version != project.PackVersion || device is null || device.Architecture != "mcs251" ||
            device.FlashOrigin != 0xff0000 || device.FlashBytes != 65536 || device.ToolsetId != project.ToolsetId ||
            device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId)
        {
            throw Invalid("工程与器件包的型号、32 位代码地址或工具链不一致。");
        }
        using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(root, Mx251Profile.ConfigurationFile), token));
        if (configuration.RootElement.GetProperty("target").GetString() != "AI8051U-34K64-32")
        {
            throw Invalid("当前器件下载固定为 32 位，不能使用 8 位固件或配置。");
        }
        return project;
    }
    private async Task<(byte[] Image, string Source, string ReceiptHash)> ReadBuildAsync(string root, CancellationToken token)
    {
        var project = await ValidateProjectAsync(root, token);
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        Mx251Profile.ValidateTools(tools);
        var lockPath = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
        var path = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(path) || !File.Exists(lockPath) || await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) !=
            new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, tools.Fingerprint))
        {
            throw Invalid("请先使用当前锁定的 MX251 工具链成功编译。");
        }
        var bytes = await File.ReadAllBytesAsync(path, token);
        var receipt = JsonSerializer.Deserialize<BuildReceipt>(bytes, JsonStore.Options) ?? throw Invalid("构建凭据为空。");
        if (receipt.Project != project || receipt.ToolFingerprint != tools.Fingerprint || receipt.Images.Length != 1 ||
            receipt.Images[0].Format != "ihex" || receipt.SourceStamp is null || receipt.SourceStamp != await Debugging.DebugSourceStamp.ComputeAsync(root, token))
        {
            throw Invalid("源码、目标或构建配置已经变化，请重新编译后下载。");
        }
        var image = receipt.Images[0];
        if (!image.RelativePath.Replace('\\', '/').StartsWith(".build/mx251/", StringComparison.Ordinal))
        {
            throw Invalid("固件不是当前 MX251 构建产物。");
        }
        var source = PathBoundary.Resolve(root, image.RelativePath);
        if (!File.Exists(source) || new FileInfo(source).Length > 1024 * 1024)
        {
            throw Invalid("固件不存在或异常过大。");
        }
        var content = await File.ReadAllBytesAsync(source, token);
        if (!Hash(content).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("HEX 已被修改，请重新编译。");
        }
        IspImage.Parse(Encoding.ASCII.GetString(content));
        return (content, source, Hash(bytes));
    }
    public async Task<Mx251IspPreparation> PrepareAsync(string directory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        using var operation = Mx251BuildStorage.Acquire(root);
        var settings = await LoadSettingsAsync(root, token);
        settings.Validate(requireDevice: true);
        var input = await ReadBuildAsync(root, token);
        if (await Mx251ClockConfiguration.ReadAsync(root, token) != settings.IrcHz)
        {
            throw Invalid("工程尚未绑定 IRC 频率，请保存下载设置并重新编译。");
        }
        var run = Mx251BuildStorage.Prepare(root, download: true);
        Directory.CreateDirectory(run);
        var image = Path.Combine(run, "firmware.hex");
        await File.WriteAllBytesAsync(image, input.Image, token);
        var result = new Mx251IspPreparation(root, input.Source, image, Hash(input.Image), input.ReceiptHash,
            Path.Combine(run, "transport.ndjson"), settings, IspImage.Parse(Encoding.ASCII.GetString(input.Image)).Code.Length);
        await JsonStore.WriteAsync(Path.Combine(run, "preparation.json"), result, token);
        return result;
    }
    public Task<Mx251IspReport> DownloadPreparedAsync(Mx251IspPreparation prepared, IProgress<string>? output = null, CancellationToken token = default)
        => Task.Run(() => DownloadCoreAsync(prepared, output, token), token);

    private async Task<Mx251IspReport> DownloadCoreAsync(Mx251IspPreparation prepared, IProgress<string>? output, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token))
        {
            throw Invalid("MX251 下载正在进行。");
        }
        try
        {
            using var operation = Mx251BuildStorage.Acquire(prepared.ProjectRoot);
            var input = await ReadBuildAsync(prepared.ProjectRoot, token);
            if (input.ReceiptHash != prepared.ReceiptSha256 || input.Source != prepared.SourceImage || Hash(input.Image) != prepared.ImageSha256 ||
                await LoadSettingsAsync(prepared.ProjectRoot, token) != prepared.Settings)
            {
                throw Invalid("准备后的构建或下载选择已经变化，请重新下载。");
            }
            var run = Path.GetDirectoryName(prepared.ImagePath)!;
            var relative = Path.GetRelativePath(prepared.ProjectRoot, run).Replace('\\', '/');
            if (!relative.StartsWith(".build/mx251-isp/", StringComparison.Ordinal) || relative.Split('/').Length != 3 ||
                prepared.ImagePath != PathBoundary.Resolve(prepared.ProjectRoot, relative + "/firmware.hex") ||
                prepared.LogPath != PathBoundary.Resolve(prepared.ProjectRoot, relative + "/transport.ndjson"))
            {
                throw Invalid("下载快照路径无效。");
            }
            var image = IspImage.Load(prepared.ImagePath);
            if (!image.Sha256.Equals(prepared.ImageSha256, StringComparison.OrdinalIgnoreCase) || image.Code.Length != prepared.CodeBytes)
            {
                throw Invalid("下载快照被修改；未打开硬件。");
            }
            var device = SelectDevice(prepared.Settings, await ListDevicesAsync(token));
            token.ThrowIfCancellationRequested();
            using var ownership = ProbeLease.Acquire();
            using var log = new StreamWriter(new FileStream(prepared.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            long sequence = 0;
            void Event(object value)
            {
                log.WriteLine(JsonSerializer.Serialize(new
                {
                    sequence = ++sequence,
                    hostUtc = DateTimeOffset.UtcNow,
                    value
                }));
                output?.Report(JsonSerializer.Serialize(value));
            }
            Event(new
            {
                stage = "open",
                device,
                firmwareSha256 = image.Sha256,
                cpuMode = 32,
                ircHz = prepared.Settings.IrcHz,
                options = "write-verified-profile"
            });
            try
            {
                using var connection = backend.Open(device, (direction, report) => log.WriteLine(JsonSerializer.Serialize(new
                {
                    sequence = ++sequence,
                    hostUtc = DateTimeOffset.UtcNow,
                    direction,
                    hex = Convert.ToHexString(report)
                })));
                var protocol = new BridgeProtocol(connection.Transport, Event);
                // 此入口没有 preserve/8 位分支：每次下载都写入已验证 32 位配置，并要求配置回读一致。
                var result = await IspSession.ExecuteAsync(protocol, image, new(1, "AI8051U-34K64", 32, prepared.Settings.IrcHz), Event, token);
                Event(new
                {
                    stage = "complete",
                    result
                });
                var report = new Mx251IspReport(true, $"下载完成：全部写块 ACK，固定 32 位；目标 IRC {prepared.Settings.IrcHz / 1000000m:0.######} MHz，校准后 {result.Target.IrcHz / 1000000m:0.######} MHz，配置已读取核对。已发送运行命令，未进行 Flash 回读或程序功能验证。", prepared.LogPath, result);
                await JsonStore.WriteAsync(Path.Combine(run, "result.json"), report, CancellationToken.None);
                return report;
            }
            catch (Exception error)
            {
                Event(new
                {
                    stage = "session-failed",
                    error = error.ToString()
                });
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
