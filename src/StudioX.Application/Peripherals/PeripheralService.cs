namespace StudioX.Application.Peripherals;

using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Engine.Svd;
using StudioX.Foundation;

/// <summary>用户明确核对器件后绑定 SVD；存入独立用户目录，不把开发者绝对路径写入工程。</summary>
public sealed class PeripheralService(DebugSessionService debugger, string dataDirectory)
{
    private string BindingPath(string project) => Path.Combine(dataDirectory, "svd", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant()))), "binding.json");
    public async Task<PeripheralDocument> ImportAsync(string project, string file, string confirmedDeviceId, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        var manifest = await ProjectService.ReadAsync(root, token);
        if (manifest.DeviceId != confirmedDeviceId) { throw new StudioXException("SVD_DEVICE", "确认的器件与工程不同。"); }
        var device = await SvdParser.LoadAsync(file, token);
        var folder = Path.GetDirectoryName(BindingPath(root))!;
        Directory.CreateDirectory(folder);
        var saved = Path.Combine(folder, device.Sha256 + ".svd");
        // 再次读取校验，防止选择后文件变化；按内容寻址，用户原文件不修改。
        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is < 1 or > 32 * 1024 * 1024) { throw new StudioXException("SVD_SIZE", "SVD 大小已变化或超过限制。"); }
        var bytes = new byte[(int)source.Length];
        await source.ReadExactlyAsync(bytes, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != device.Sha256) { throw new StudioXException("SVD_CHANGED", "SVD 在导入时已变化。"); }
        await File.WriteAllBytesAsync(saved, bytes, token);
        var binding = new PeripheralBinding(1, confirmedDeviceId, Path.GetFileName(saved), device.Sha256);
        await JsonStore.WriteAsync(BindingPath(root), binding, token);
        return new(root, binding, device);
    }
    public async Task<PeripheralDocument?> OpenAsync(string project, CancellationToken token = default)
    {
        var path = BindingPath(project);
        if (!File.Exists(path)) { return null; }
        var binding = await JsonStore.ReadAsync<PeripheralBinding>(path, token);
        if (binding.FormatVersion != 1 || binding.DeviceId != (await ProjectService.ReadAsync(project, token)).DeviceId) { throw new StudioXException("SVD_BINDING", "SVD 绑定与当前工程器件不一致，请重新导入。"); }
        var device = await SvdParser.LoadAsync(PathBoundary.Resolve(Path.GetDirectoryName(path)!, binding.File), token);
        if (device.Sha256 != binding.Sha256) { throw new StudioXException("SVD_HASH", "SVD 内容校验失败。"); }
        return new(Path.GetFullPath(project), binding, device);
    }
    private void Check(PeripheralDocument document, SvdRegister register)
    {
        if (!document.Device.Registers.Contains(register) || !string.Equals(document.Project, debugger.ProjectDirectory, StringComparison.OrdinalIgnoreCase))
        { throw new StudioXException("SVD_SESSION", "请选择当前工程已绑定的寄存器。"); }
    }
    public async Task<PeripheralReading> ReadAsync(PeripheralDocument document, SvdRegister register, bool acknowledgeSideEffects = false, CancellationToken token = default)
    {
        Check(document, register);
        var revision = debugger.PeripheralRevision;
        var requested = DateTimeOffset.UtcNow;
        var value = await debugger.ReadPeripheralAsync(document.Binding.DeviceId, register, revision, acknowledgeSideEffects, token);
        if (revision != debugger.PeripheralRevision) { throw new StudioXException("SVD_STALE", "目标状态已变化，读数作废。"); }
        return new(register.Path, value, requested, revision);
    }
    public PeripheralWrite PreviewWrite(PeripheralDocument document, SvdRegister register, ulong value)
    {
        Check(document, register);
        if (!register.CanWrite || register.Width < 64 && value >= (1UL << register.Width)) { throw new StudioXException("SVD_WRITE", "该寄存器不支持直接写入，或数值超出位宽。"); }
        return new(document, register, value, debugger.PeripheralRevision);
    }
    public Task WriteAsync(PeripheralWrite preview, CancellationToken token = default)
    {
        Check(preview.Document, preview.Register);
        return debugger.WritePeripheralAsync(preview.Document.Binding.DeviceId, preview.Register, preview.Revision, preview.Value, token);
    }
}
