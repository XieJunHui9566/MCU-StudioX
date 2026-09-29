using System.Net;
using System.Text.Json;
using StudioX.Application;
using StudioX.Foundation;
using StudioX.Packages;

// 通过 IDE 的真实同步和导入服务验收发布目录；HTTP 只读本地快照，不连接硬件或用户包仓库。
if (args.Length != 2) { throw new ArgumentException("Usage: StudioX.Arm32PublicValidation <public-repository> <new-output-directory>"); }
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) || File.Exists(output)) { throw new ArgumentException("Use a new output directory."); }
Directory.CreateDirectory(output);
using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "index.json")));
var expected = document.RootElement.GetProperty("packs").GetArrayLength();
using var handler = new SnapshotHandler(source);
using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
var repository = new PackRepository(Path.Combine(output, "repository"));
using var service = new GitHubPackSyncService(repository, client);
var check = await service.CheckForUpdatesAsync();
if (check.Updates.Count != expected || check.UpToDate != 0 || handler.ArchiveRequests != 0)
{
    throw new InvalidOperationException("Catalog check failed or downloaded archives unexpectedly.");
}
Console.WriteLine($"PASS catalog: {expected} entries, metadata only.");
var first = await service.SyncAsync(new ImportProgress());
if (first.Imported != expected || first.Failures.Count != 0 || handler.ArchiveRequests != expected)
{
    await File.WriteAllTextAsync(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(first, JsonStore.Options));
    throw new InvalidOperationException($"Import failed: {first.Imported}/{expected}; {JsonSerializer.Serialize(first.Failures)}");
}
check = await service.CheckForUpdatesAsync();
var second = await service.SyncAsync();
if (check.UpToDate != expected || check.Updates.Count != 0 || second.Skipped != expected || second.Imported != 0 ||
    second.Failures.Count != 0 || handler.ArchiveRequests != expected)
{
    throw new InvalidOperationException("Repeat synchronization must skip every installed pack without downloading.");
}
var report = new
{
    status = "PASS", offline = true, hardwareAccessed = false, packages = expected,
    imported = first.Imported, repeatSkipped = second.Skipped, archiveRequests = handler.ArchiveRequests,
    failures = first.Failures.Count, method = "Installed IDE GitHubPackSyncService and PackRepository; local HTTP snapshot"
};
await File.WriteAllTextAsync(Path.Combine(output, "public-import.json"), JsonSerializer.Serialize(report, JsonStore.Options));
Console.WriteLine(JsonSerializer.Serialize(report));

internal sealed class ImportProgress : IProgress<RemotePackSyncProgress>
{
    private int previous;
    public void Report(RemotePackSyncProgress value)
    {
        if (value.Processed >= previous + 25 || value.Processed == value.Total && value.Total > 0)
        {
            previous = value.Processed;
            Console.WriteLine($"Import progress: {value.Processed}/{value.Total}");
        }
    }
}

internal sealed class SnapshotHandler(string source) : HttpMessageHandler
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public int ArchiveRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
        if (uri.AbsoluteUri == "https://api.github.com/repos/XieJunHui9566/MCU-StudioX-MCUPacks/commits/main")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"sha\":\"" + Commit + "\"}") });
        }
        var prefix = "/XieJunHui9566/MCU-StudioX-MCUPacks/" + Commit + "/";
        if (uri.Host != "raw.githubusercontent.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected request: " + uri);
        }
        var relative = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        var path = PathBoundary.Resolve(source, relative);
        if (relative.EndsWith(".mcupack", StringComparison.Ordinal)) { ArchiveRequests++; }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
    }
}
