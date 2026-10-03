using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Packages;

/// <summary>用待发布的真实目录与包验证增量同步；HTTP 固定为本地快照，不访问网络。</summary>
internal static class RemotePackSetChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(string packDirectory, string output)
    {
        var root = Path.GetDirectoryName(packDirectory)!;
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "index.json")));
        var entries = index.RootElement.GetProperty("packs").EnumerateArray()
            .Where(entry => entry.GetProperty("id").GetString()!.StartsWith("espressif.", StringComparison.Ordinal) &&
                entry.GetProperty("id").GetString() != "espressif.esp8266").Select(entry => entry.Clone()).ToArray();
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        Check(entries.Length == 24 && entries.All(entry => entry.GetProperty("retainVersion").GetBoolean()),
            "the publication catalog explicitly retains all 24 SDK/target identities");
        var repository = new PackRepository(Path.Combine(output, "remote-repository"));
        var archives = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var relative = entry.GetProperty("path").GetString()!;
            var path = Path.Combine(root, relative);
            archives.Add(relative, await File.ReadAllBytesAsync(path));
            if (entry.GetProperty("version").GetString() is "0.1.1" or "0.4.0")
            {
                await repository.ImportAsync(path);
            }
        }
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var handler = new LocalCatalogHandler(commit, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, packs = entries }), archives);
        using var client = new HttpClient(handler);
        using var sync = new GitHubPackSyncService(repository, client);
        var check = await sync.CheckForUpdatesAsync();
        Check(check.UpToDate == 12 && check.Updates.Count == 12 && check.Updates.All(update => update.InstalledVersion is null),
            "installed 5.5.4 and 6.1.0 do not hide the 12 missing 5.5.5/6.0.3 packs");
        Check(handler.ArchiveRequests == 0, "update check reads metadata only");
        var result = await sync.SyncAsync();
        Check(result.Imported == 12 && result.Skipped == 12 && result.Failures.Count == 0,
            "incremental sync imports every missing retained version from the real catalog");
        Check(handler.ArchiveRequests == 12, "incremental sync downloads each missing identity once");
        var installed = await repository.ListCatalogAsync();
        Check(installed.Count == 24 && PackCatalogPolicy.SelectCurrentVersions(installed).Count == 24,
            "all actual SDK identities survive visibility and retention policy");
        result = await sync.SyncAsync();
        Check(result.Imported == 0 && result.Skipped == 24 && result.Failures.Count == 0 && handler.ArchiveRequests == 12,
            "repeat real-catalog sync downloads no archives");
        return checks;
    }

    private sealed class LocalCatalogHandler(string commit, byte[] index, IReadOnlyDictionary<string, byte[]> archives) : HttpMessageHandler
    {
        private const string Repository = "XieJunHui9566/MCU-StudioX-MCUPacks";
        public int ArchiveRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? throw new InvalidOperationException("Missing URL.");
            var prefix = $"https://raw.githubusercontent.com/{Repository}/{commit}/";
            byte[] content;
            if (url == $"https://api.github.com/repos/{Repository}/commits/main")
            {
                content = Encoding.UTF8.GetBytes("{\"sha\":\"" + commit + "\"}");
            }
            else if (url == prefix + "index.json")
            {
                content = index;
            }
            else if (url.StartsWith(prefix, StringComparison.Ordinal) && archives.TryGetValue(url[prefix.Length..], out var bytes))
            {
                ArchiveRequests++;
                content = bytes;
            }
            else
            {
                throw new InvalidOperationException("Unexpected publication URL: " + url);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
