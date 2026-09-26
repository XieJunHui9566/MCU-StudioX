namespace StudioX.Application;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record RemotePackSyncFailure(string Path, string Message);
public sealed record RemotePackSyncResult(string CommitSha, int Imported, int Skipped,
    IReadOnlyList<RemotePackSyncFailure> Failures);
public sealed record RemotePackSyncProgress(int Total, int Processed, string? CurrentPath,
    string Stage, int Imported, int Skipped, int Failed);
public sealed record RemotePackUpdate(string Id, string Version, string? InstalledVersion, string Path);
public sealed record RemotePackCheckResult(string CommitSha, int UpToDate,
    IReadOnlyList<RemotePackUpdate> Updates);

/// <summary>从固定的公开 GitHub 仓库下载器件包；同一轮始终使用同一个提交快照。</summary>
public sealed class GitHubPackSyncService : IDisposable
{
    private const string Owner = "XieJunHui9566";
    private const string Repository = "MCU-StudioX-MCUPacks";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private static readonly Uri CommitUri = new($"https://api.github.com/repos/{Owner}/{Repository}/commits/main");
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private readonly PackRepository packs;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly SemaphoreSlim syncGate = new(1, 1);

    /// <summary>HTTP 客户端可由离线测试注入；普通用户不能指定下载站点或仓库。</summary>
    public GitHubPackSyncService(PackRepository packs, HttpClient? client = null)
    {
        this.packs = packs ?? throw new ArgumentNullException(nameof(packs));
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            // 每次请求由关联的取消令牌限定；避免 HttpClient 默认 100 秒上限覆盖单包下载时限。
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        ownsClient = client is null;
        if (ownsClient) this.client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
        syncGate.Dispose();
    }

    /// <summary>只检查公开目录与本地版本，不下载或导入器件包。</summary>
    public async Task<RemotePackCheckResult> CheckForUpdatesAsync(CancellationToken token = default)
    {
        await syncGate.WaitAsync(token);
        try
        {
            var commit = await GetCommitAsync(token);
            var latest = await GetLatestEntriesAsync(commit, token);
            var installed = await GetInstalledVersionsAsync(token);
            var updates = new List<RemotePackUpdate>();
            var upToDate = 0;
            foreach (var entry in latest)
            {
                token.ThrowIfCancellationRequested();
                installed.TryGetValue(entry.Id, out var localVersion);
                if (localVersion is not null && CompareVersions(localVersion, entry.Version) >= 0)
                    upToDate++;
                else
                    updates.Add(new(entry.Id, entry.Version, localVersion, entry.Path));
            }
            return new(commit, upToDate, updates);
        }
        finally { syncGate.Release(); }
    }

    public async Task<RemotePackSyncResult> SyncAsync(IProgress<RemotePackSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await syncGate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new(0, 0, null, "读取 GitHub 器件包目录", 0, 0, 0));
            var commit = await GetCommitAsync(cancellationToken);
            var latest = await GetLatestEntriesAsync(commit, cancellationToken);
            var installed = await GetInstalledVersionsAsync(cancellationToken);

            var imported = 0;
            var skipped = 0;
            var failures = new List<RemotePackSyncFailure>();
            var processed = 0;
            foreach (var entry in latest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (installed.TryGetValue(entry.Id, out var localVersion) &&
                    CompareVersions(localVersion, entry.Version) >= 0)
                {
                    skipped++;
                    processed++;
                    progress?.Report(new(latest.Length, processed, entry.Path, "已安装或本地版本更新", imported, skipped, failures.Count));
                    continue;
                }

                var temporary = Path.Combine(packs.RootDirectory, ".remote-" + Guid.NewGuid().ToString("N") + ".mcupack");
                try
                {
                    Directory.CreateDirectory(packs.RootDirectory);
                    progress?.Report(new(latest.Length, processed, entry.Path, "下载中", imported, skipped, failures.Count));
                    await DownloadAsync(commit, entry, temporary, cancellationToken);
                    // 在导入之前核对目录身份，避免错误索引把合法但不匹配的包安装进去。
                    await CheckManifestIdentityAsync(temporary, entry, cancellationToken);
                    progress?.Report(new(latest.Length, processed, entry.Path, "校验并导入", imported, skipped, failures.Count));
                    var pack = await packs.ImportAsync(temporary, cancellationToken);
                    if (pack.Manifest.Id != entry.Id || pack.Manifest.Version != entry.Version)
                        throw new StudioXException("PACK_REMOTE_ID", "导入后器件包身份与 GitHub 目录不一致。");
                    installed[entry.Id] = entry.Version;
                    imported++;
                    processed++;
                    progress?.Report(new(latest.Length, processed, entry.Path, "已导入", imported, skipped, failures.Count));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    failures.Add(new(entry.Path, "下载或导入超时。"));
                    processed++;
                    progress?.Report(new(latest.Length, processed, entry.Path, "失败", imported, skipped, failures.Count));
                }
                catch (Exception ex)
                {
                    failures.Add(new(entry.Path, ex.Message));
                    processed++;
                    progress?.Report(new(latest.Length, processed, entry.Path, "失败", imported, skipped, failures.Count));
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return new(commit, imported, skipped, failures);
        }
        finally { syncGate.Release(); }
    }

    private async Task<RemotePackIndexEntry[]> GetLatestEntriesAsync(string commit, CancellationToken token)
    {
        var entries = await GetIndexAsync(commit, token);
        return entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(entry => entry.Version, Comparer<string>.Create(CompareVersions))!)
            .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<Dictionary<string, string>> GetInstalledVersionsAsync(CancellationToken token) =>
        (await packs.ListCatalogAsync(token))
        .GroupBy(pack => pack.Manifest.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key,
            group => group.MaxBy(pack => pack.Manifest.Version, Comparer<string>.Create(CompareVersions))!.Manifest.Version,
            StringComparer.OrdinalIgnoreCase);

    private async Task<string> GetCommitAsync(CancellationToken token)
    {
        var bytes = await ReadMetadataAsync(CommitUri, "GitHub 提交", token);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sha", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { } sha ||
            !Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant))
            throw new StudioXException("PACK_REMOTE_COMMIT", "GitHub 没有返回有效的仓库提交哈希。");
        return sha.ToLowerInvariant();
    }

    private async Task<RemotePackIndexEntry[]> GetIndexAsync(string commit, CancellationToken token)
    {
        var bytes = await ReadMetadataAsync(RawUri(commit, "index.json"), "器件包目录", token);
        RemotePackIndex? index;
        try { index = JsonSerializer.Deserialize<RemotePackIndex>(bytes, JsonStore.Options); }
        catch (JsonException ex) { throw new StudioXException("PACK_REMOTE_INDEX", "GitHub 器件包目录不是有效 JSON：" + ex.Message); }
        if (index is not { FormatVersion: 1, Packs: { Count: > 0 and <= 4096 } })
            throw new StudioXException("PACK_REMOTE_INDEX", "GitHub 器件包目录格式或包数量无效。");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Packs)
        {
            if (entry is null || entry.Path is null || entry.Id is null || entry.Version is null || entry.Sha256 is null ||
                entry.Id.Length > 80 || entry.Version.Length > 80 || entry.Path.Length > 240 ||
                !Regex.IsMatch(entry.Path, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}/[A-Za-z0-9][A-Za-z0-9._-]*\\.mcupack\\z", RegexOptions.CultureInvariant) ||
                !entry.Path.EndsWith("/" + entry.Id + "-" + entry.Version + ".mcupack", StringComparison.Ordinal) ||
                entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit) ||
                entry.Size is <= 0 or > MaximumArchiveBytes ||
                !paths.Add(entry.Path) || !identities.Add(entry.Id + "\n" + entry.Version))
                throw new StudioXException("PACK_REMOTE_INDEX", "GitHub 器件包目录含无效或重复的路径、身份、大小或哈希。");
            PackValidator.Token(entry.Id);
            PackValidator.Version(entry.Version);
        }
        return index.Packs.ToArray()!;
    }

    private async Task<byte[]> ReadMetadataAsync(Uri uri, string description, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MCU-StudioX/0.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new StudioXException("PACK_REMOTE_HTTP", $"读取{description}失败（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                throw new StudioXException("PACK_REMOTE_LIMIT", $"{description}超过大小限制。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > MaximumMetadataBytes)
                    throw new StudioXException("PACK_REMOTE_LIMIT", $"{description}超过大小限制。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new StudioXException("PACK_REMOTE_TIMEOUT", $"读取{description}超时。"); }
    }

    private async Task DownloadAsync(string commit, RemotePackIndexEntry entry, string target, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DownloadTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, RawUri(commit, entry.Path!));
        request.Headers.UserAgent.ParseAdd("MCU-StudioX/0.2");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new StudioXException("PACK_REMOTE_HTTP", $"下载器件包失败（HTTP {(int)response.StatusCode}）。");
        if (response.Content.Headers.ContentLength is { } length && length != entry.Size)
            throw new StudioXException("PACK_REMOTE_SIZE", "器件包响应长度与 GitHub 目录不一致。");

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long written = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            written += count;
            if (written > entry.Size || written > MaximumArchiveBytes)
                throw new StudioXException("PACK_REMOTE_SIZE", "器件包下载长度超过 GitHub 目录声明。");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        if (written != entry.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("PACK_REMOTE_HASH", "器件包 SHA-256 与 GitHub 目录不一致。");
    }

    private static async Task CheckManifestIdentityAsync(string archive, RemotePackIndexEntry entry, CancellationToken token)
    {
        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new StudioXException("PACK_REMOTE_ID", "下载的器件包缺少清单。");
        if (manifestEntry.Length > MaximumMetadataBytes)
            throw new StudioXException("PACK_REMOTE_LIMIT", "下载的器件包清单过大。");
        await using var source = manifestEntry.Open();
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > MaximumMetadataBytes)
                throw new StudioXException("PACK_REMOTE_LIMIT", "下载的器件包清单解压后过大。");
            bytes.Write(buffer, 0, count);
        }
        var manifest = JsonSerializer.Deserialize<PackManifest>(bytes.ToArray(), JsonStore.Options)
            ?? throw new StudioXException("PACK_REMOTE_ID", "下载的器件包清单无效。");
        if (manifest.Id != entry.Id || manifest.Version != entry.Version)
            throw new StudioXException("PACK_REMOTE_ID", "器件包清单的 ID/版本与 GitHub 目录不一致。");
    }

    private static Uri RawUri(string commit, string path) =>
        new($"https://raw.githubusercontent.com/{Owner}/{Repository}/{commit}/{path}");

    private static int CompareVersions(string left, string right) => PackVersion.Compare(left, right);

    private sealed record RemotePackIndex(int FormatVersion, IReadOnlyList<RemotePackIndexEntry>? Packs);
    private sealed record RemotePackIndexEntry(string Path, string Id, string Version, string Sha256, long Size);
}
