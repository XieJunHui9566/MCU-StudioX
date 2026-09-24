using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length != 1) throw new ArgumentException("Usage: StudioX.RemotePackValidation <new-output-directory>");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new ArgumentException("Use a new output directory.");
Directory.CreateDirectory(output);
const string commit = "0123456789abcdef0123456789abcdef01234567";
const string packId = "test.remote";
var first = await CreatePackAsync("1.0.0");
var latest = await CreatePackAsync("2.0.0");
var index = MakeIndex(first, latest);

var handler = new ScriptedHandler(commit, index, new Dictionary<string, byte[]>
{
    [first.Path] = first.Bytes,
    [latest.Path] = latest.Bytes
});
var repository = new PackRepository(Path.Combine(output, "installed"));
using (var client = new HttpClient(handler))
using (var sync = new GitHubPackSyncService(repository, client))
{
    var result = await sync.SyncAsync();
    Require(result.Imported == 1 && result.Skipped == 0 && result.Failures.Count == 0, "first sync imports latest pack");
    Require((await repository.ListCatalogAsync()).Single().Manifest.Version == "2.0.0", "latest version installed");
    Require(!handler.Requests.Any(uri => uri.AbsolutePath.EndsWith(first.Path, StringComparison.Ordinal)), "old version never downloaded");
    Console.WriteLine("PASS: latest version imported from commit-pinned URL");

    handler.Requests.Clear();
    result = await sync.SyncAsync();
    Require(result.Imported == 0 && result.Skipped == 1 && result.Failures.Count == 0, "second sync skips installed version");
    Require(!handler.Requests.Any(uri => uri.AbsolutePath.EndsWith(".mcupack", StringComparison.Ordinal)), "second sync avoids archive download");
    Console.WriteLine("PASS: repeat sync is incremental");
}

var wrongHash = latest with { Sha256 = new string('0', 64) };
var damagedRepository = new PackRepository(Path.Combine(output, "damaged-installed"));
using (var client = new HttpClient(new ScriptedHandler(commit, MakeIndex(wrongHash),
           new Dictionary<string, byte[]> { [latest.Path] = latest.Bytes })))
using (var sync = new GitHubPackSyncService(damagedRepository, client))
{
    var result = await sync.SyncAsync();
    Require(result.Imported == 0 && result.Failures.Count == 1, "bad archive hash reported");
    Require((await damagedRepository.ListCatalogAsync()).Count == 0, "bad archive not installed");
    Require(!Directory.EnumerateFiles(damagedRepository.RootDirectory, ".remote-*", SearchOption.TopDirectoryOnly).Any(),
        "failed download temporary file removed");
    Console.WriteLine("PASS: altered archive rejected and cleaned up");
}

var invalidPath = latest with { Path = "Puya/../escape.mcupack" };
var invalidHandler = new ScriptedHandler(commit, MakeIndex(invalidPath), new Dictionary<string, byte[]>());
using (var client = new HttpClient(invalidHandler))
using (var sync = new GitHubPackSyncService(new PackRepository(Path.Combine(output, "invalid-installed")), client))
{
    try { await sync.SyncAsync(); throw new InvalidOperationException("Traversal path was accepted."); }
    catch (StudioXException ex) when (ex.Code == "PACK_REMOTE_INDEX") { }
    Require(!invalidHandler.Requests.Any(uri => uri.AbsolutePath.EndsWith(".mcupack", StringComparison.Ordinal)),
        "invalid path rejected before download");
    Console.WriteLine("PASS: unsafe index path rejected before download");
}

var newerRepository = new PackRepository(Path.Combine(output, "newer-installed"));
var newer = await CreatePackAsync("3.0.0");
await newerRepository.ImportAsync(newer.Archive);
var olderHandler = new ScriptedHandler(commit, MakeIndex(latest),
    new Dictionary<string, byte[]> { [latest.Path] = latest.Bytes });
using (var client = new HttpClient(olderHandler))
using (var sync = new GitHubPackSyncService(newerRepository, client))
{
    var result = await sync.SyncAsync();
    Require(result.Imported == 0 && result.Skipped == 1, "newer local version kept");
    Require((await newerRepository.ListCatalogAsync()).Single().Manifest.Version == "3.0.0", "no downgrade");
    Console.WriteLine("PASS: newer local version is preserved");
}

await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: remote pack validation\n");

async Task<Fixture> CreatePackAsync(string version)
{
    var source = Path.Combine(output, "source-" + version);
    Directory.CreateDirectory(source);
    await File.WriteAllTextAsync(Path.Combine(source, "main.c"), "int main(void) { for (;;) {} }\n");
    await File.WriteAllTextAsync(Path.Combine(source, "support.c"), "int example;\n");
    await File.WriteAllTextAsync(Path.Combine(source, "link.ld"), "MEMORY { FLASH(rx): ORIGIN=0x08000000, LENGTH=128K }\n");
    var device = new DeviceDefinition("test-device", "Test device", "arm", 0x08000000, 131072,
        0x20000000, 20480, "test.toolset", "1.0.0", "test-gcc", ["-mcpu=cortex-m3", "-mthumb"],
        [], [], ["support.c"], "link.ld", [], [],
        [new ProjectTemplate("bare", "Bare", "Test", "main.c")]);
    await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"),
        new PackManifest(1, packId, version, "Remote fixture", "Test", [device]));
    var path = $"Puya/{packId}-{version}.mcupack";
    var archive = Path.Combine(output, $"fixture-{version}.mcupack");
    await PackArchiveWriter.WriteAsync(source, archive);
    var bytes = await File.ReadAllBytesAsync(archive);
    return new(path, packId, version, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, archive, bytes);
}

static byte[] MakeIndex(params Fixture[] entries) => JsonSerializer.SerializeToUtf8Bytes(new
{
    formatVersion = 1,
    packs = entries.Select(entry => new { path = entry.Path, id = entry.Id, version = entry.Version,
        sha256 = entry.Sha256, size = entry.Size }).ToArray()
});

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}

sealed record Fixture(string Path, string Id, string Version, string Sha256, long Size, string Archive, byte[] Bytes);

sealed class ScriptedHandler(string commit, byte[] index, IReadOnlyDictionary<string, byte[]> archives) : HttpMessageHandler
{
    private const string Repo = "XieJunHui9566/MCU-StudioX-MCUPacks";
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
        Requests.Add(uri);
        byte[] payload;
        if (uri.AbsoluteUri == $"https://api.github.com/repos/{Repo}/commits/main")
            payload = Encoding.UTF8.GetBytes("{\"sha\":\"" + commit + "\"}");
        else if (uri.AbsoluteUri == $"https://raw.githubusercontent.com/{Repo}/{commit}/index.json")
            payload = index;
        else if (uri.AbsoluteUri.StartsWith($"https://raw.githubusercontent.com/{Repo}/{commit}/", StringComparison.Ordinal) &&
                 archives.TryGetValue(uri.AbsoluteUri[($"https://raw.githubusercontent.com/{Repo}/{commit}/").Length..], out var bytes))
            payload = bytes;
        else
            throw new InvalidOperationException("Unexpected remote URL: " + uri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
}
