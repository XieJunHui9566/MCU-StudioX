namespace StudioX.Application.Distribution;

using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;
using StudioX.Extensions;
using StudioX.Packages;

/// <summary>显式读取目录和校验下载；目录发布者声明与受信任密钥验证分别呈现。</summary>
public sealed class DistributionService : IDisposable
{
    private readonly HttpClient http;
    private readonly string cache;
    public DistributionService(string dataDirectory, HttpMessageHandler? handler = null)
    {
        cache = Path.Combine(Path.GetFullPath(dataDirectory), "distribution-cache");
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MCU-StudioX/1.0");
    }
    public async Task<DistributionListing> ReadAsync(string source, string? publisherKey = null, CancellationToken token = default)
    {
        byte[] bytes;
        var online = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https";
        if (online)
        {
            ValidateUrl(uri!);
            bytes = await GetBytesAsync(uri!, 1024 * 1024, token);
        }
        else
        {
            source = Path.GetFullPath(source);
            if (new FileInfo(source).Length > 1024 * 1024) { throw new StudioXException("CATALOG_SIZE", "目录超过 1 MiB。"); }
            bytes = await File.ReadAllBytesAsync(source, token);
        }
        var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var catalog = JsonSerializer.Deserialize<DistributionCatalog>(bytes.AsSpan(offset), JsonStore.Options)
            ?? throw new StudioXException("CATALOG_FORMAT", "目录为空。");
        if (catalog.FormatVersion != 1 || string.IsNullOrWhiteSpace(catalog.Publisher) || catalog.Entries is null || catalog.Entries.Length > 1000) { throw new StudioXException("CATALOG_FORMAT", "目录格式、发布者或条目数量无效。"); }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            PackValidator.Token(entry.Id);
            PackValidator.Version(entry.Version);
            if (entry.Kind is not ("tool" or "plugin" or "component") || !identities.Add(entry.Kind + "/" + entry.Id + "/" + entry.Version)
                || entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit)
                || entry.DownloadBytes < 1 || entry.DownloadBytes > 8L * 1024 * 1024 * 1024 || entry.InstalledBytes < 1 || entry.InstalledBytes > 32L * 1024 * 1024 * 1024
                || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.License) || string.IsNullOrWhiteSpace(entry.ReleaseNotes)) { throw new StudioXException("CATALOG_ENTRY", "目录条目身份、大小、许可证或更新说明无效。"); }
            ValidateUrl(new Uri(entry.SourceUrl));
            if (entry.Kind == "plugin" && entry.PluginApi is < 1 or > 3) { throw new StudioXException("CATALOG_API", "插件 API 与本 IDE 不兼容。"); }
            if (online) { ValidateUrl(new Uri(uri!, entry.Archive)); }
            else if (Uri.TryCreate(entry.Archive, UriKind.Absolute, out var remote) && remote.Scheme == "https") { ValidateUrl(remote); }
            else { _ = PathBoundary.Resolve(Path.GetDirectoryName(source)!, entry.Archive); }
        }
        var verification = "发布者自述，未验证签名；SHA-256 仅校验内容完整性";
        if (publisherKey is not null)
        {
            if (new FileInfo(publisherKey).Length > 64 * 1024) { throw new StudioXException("CATALOG_KEY", "发布者公钥文件过大。"); }
            var signature = online ? await GetBytesAsync(new Uri(source + ".sig"), 8192, token) : await File.ReadAllBytesAsync(source + ".sig", token);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(await File.ReadAllTextAsync(publisherKey, token));
            if (rsa.KeySize is < 2048 or > 8192) { throw new StudioXException("CATALOG_KEY", "RSA 公钥须为 2048–8192 位。"); }
            if (!rsa.VerifyData(bytes, Convert.FromBase64String(Encoding.ASCII.GetString(signature).Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) { throw new StudioXException("CATALOG_SIGNATURE", "目录签名与所选发布者公钥不匹配。"); }
            verification = "签名匹配所选公钥 · 密钥 SHA-256 " + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        }
        return new(catalog, source, Convert.ToHexString(SHA256.HashData(bytes)), verification);
    }

    /// <summary>目录下载后的插件身份校验不执行入口程序集；完整内容校验由插件仓储在安装时执行。</summary>
    public static Task ValidatePluginArchiveAsync(string archive, DistributionEntry item, CancellationToken token = default) => Task.Run(async () =>
    {
        await VerifyAsync(archive, item, token);
        using var zip = ZipFile.OpenRead(archive);
        var description = zip.GetEntry("plugin.json") ?? throw new StudioXException("PLUGIN_FORMAT", "缺少插件清单。");
        if (description.Length > 1024 * 1024) { throw new StudioXException("PLUGIN_FORMAT", "插件清单过大。"); }
        using var reader = new StreamReader(description.Open());
        var manifest = JsonSerializer.Deserialize<PluginManifest>(await reader.ReadToEndAsync(token), JsonStore.Options)
            ?? throw new StudioXException("PLUGIN_FORMAT", "插件清单为空。");
        if (item.Kind != "plugin" || manifest.Id != item.Id || manifest.Version != item.Version || manifest.ApiVersion != item.PluginApi || zip.Entries.Sum(e => e.Length) != item.InstalledBytes)
        { throw new StudioXException("CATALOG_IDENTITY", "插件归档与目录声明不一致。"); }
    }, token);

    public async Task<string> DownloadAsync(DistributionListing listing, DistributionEntry entry, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!listing.Catalog.Entries.Contains(entry)) { throw new StudioXException("CATALOG_SELECTION", "条目不属于当前目录快照。"); }
        var extension = entry.Kind switch { "tool" => ".studioxtools", "plugin" => ".studioxplugin", _ => ".studioxcomponent" };
        Directory.CreateDirectory(cache);
        var destination = PathBoundary.Resolve(cache, entry.Sha256.ToUpperInvariant() + extension);
        if (File.Exists(destination)) { await VerifyAsync(destination, entry, token); return destination; }
        var temp = PathBoundary.Resolve(cache, Guid.NewGuid().ToString("N") + ".partial");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        HttpResponseMessage? response = null;
        try
        {
            Stream source;
            var online = Uri.TryCreate(listing.Source, UriKind.Absolute, out var baseUri) && baseUri.Scheme == "https";
            var archiveUri = online ? new Uri(baseUri!, entry.Archive) : Uri.TryCreate(entry.Archive, UriKind.Absolute, out var absolute) && absolute.Scheme == "https" ? absolute : null;
            if (archiveUri is not null)
            {
                ValidateUrl(archiveUri);
                response = await http.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != entry.DownloadBytes) { throw new StudioXException("DOWNLOAD_SIZE", "下载长度与目录不一致。"); }
                source = await response.Content.ReadAsStreamAsync(timeout.Token);
            }
            else { source = File.OpenRead(PathBoundary.Resolve(Path.GetDirectoryName(listing.Source)!, entry.Archive)); }
            await using (source)
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                var buffer = new byte[64 * 1024];
                long received = 0, lastProgress = 0;
                while (await source.ReadAsync(buffer, timeout.Token) is var count && count > 0)
                {
                    received += count;
                    if (received > entry.DownloadBytes) { throw new StudioXException("DOWNLOAD_SIZE", "下载超过目录声明的长度。"); }
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    if (received - lastProgress >= 1024 * 1024 || received == entry.DownloadBytes) { progress?.Report($"下载 {received:N0}/{entry.DownloadBytes:N0} 字节"); lastProgress = received; }
                }
            }
            await VerifyAsync(temp, entry, timeout.Token);
            File.Move(temp, destination);
            return destination;
        }
        finally { response?.Dispose(); if (File.Exists(temp)) { File.Delete(temp); } }
    }
    public static long RequiredFreeBytes(DistributionEntry entry) => checked(entry.DownloadBytes + 2 * entry.InstalledBytes);
    public IReadOnlyList<DistributionSpacePlan> SpacePlan(DistributionEntry entry, string installationDirectory)
    {
        var groups = new[] { (Directory: cache, Bytes: entry.DownloadBytes), (Directory: Path.GetFullPath(installationDirectory), Bytes: checked(2 * entry.InstalledBytes)) }
            .GroupBy(v => Path.GetPathRoot(v.Directory)!, StringComparer.OrdinalIgnoreCase);
        return groups.Select(g => new DistributionSpacePlan(g.Key, g.Sum(v => v.Bytes), new DriveInfo(g.Key).AvailableFreeSpace)).ToArray();
    }
    public static async Task VerifyAsync(string file, DistributionEntry entry, CancellationToken token)
    {
        await using var stream = File.OpenRead(file);
        if (stream.Length != entry.DownloadBytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) { throw new StudioXException("DOWNLOAD_HASH", "归档长度或 SHA-256 不匹配，未安装。"); }
    }
    private async Task<byte[]> GetBytesAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, timeout.Token) is var count && count > 0)
        {
            if (memory.Length + count > maximum) { throw new StudioXException("CATALOG_SIZE", "目录或签名响应过大。"); }
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    private static void ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) { throw new StudioXException("CATALOG_URL", "目录只接受无账号信息的 HTTPS URL。"); }
    }
    public void Dispose() => http.Dispose();
}
