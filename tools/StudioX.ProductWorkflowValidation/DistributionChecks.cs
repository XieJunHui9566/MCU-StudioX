namespace StudioX.ProductWorkflowValidation;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.Distribution;
using StudioX.Foundation;

internal static class DistributionChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var archive = new byte[] { 1, 2, 3, 4 };
        var entry = new DistributionEntry("plugin", "fixture.plugin", "1.0.0", "Fixture", "fixture.studioxplugin", Convert.ToHexString(SHA256.HashData(archive)), 4, 10, "NOASSERTION", "https://example.test/source", "fixture release", 3);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, "Fixture publisher", [entry]), JsonStore.Options);
        var handler = new Handler(bytes, archive);
        using var service = new DistributionService(Path.Combine(root, "distribution"), handler);
        var listing = await service.ReadAsync("https://example.test/catalog.json");
        check(listing.Verification.Contains("未验证签名") && listing.CatalogSha256 == Convert.ToHexString(SHA256.HashData(bytes)), "unsigned catalog distinguishes content hash from publisher authentication");
        var file = await service.DownloadAsync(listing, listing.Catalog.Entries.Single());
        check((await File.ReadAllBytesAsync(file)).SequenceEqual(archive), "explicit remote download is size and SHA-256 checked before cache publication");
        var requests = handler.Requests;
        check(await service.DownloadAsync(listing, listing.Catalog.Entries.Single()) == file && handler.Requests == requests, "validated cache reused without another network request");
        check(DistributionService.RequiredFreeBytes(entry) == 24 && service.SpacePlan(entry, root).All(p => p.RequiredBytes > 0), "installation space includes downloaded archive and staging/rollback allowance");
        using var rsa = RSA.Create(2048);
        var key = Path.Combine(root, "publisher.pub.pem");
        await File.WriteAllTextAsync(key, rsa.ExportSubjectPublicKeyInfoPem());
        handler.Signature = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        var verified = await service.ReadAsync("https://example.test/catalog.json", key);
        check(verified.Verification.Contains("签名匹配"), "catalog signature verifies against explicitly selected publisher public key");
        handler.Catalog = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, "Changed publisher", [entry]), JsonStore.Options);
        await Reject(() => service.ReadAsync("https://example.test/catalog.json", key), "CATALOG_SIGNATURE", "tampered signed catalog rejected", check);
        handler.Catalog = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, "Fixture", [entry with { PluginApi = 99 }]), JsonStore.Options);
        await Reject(() => service.ReadAsync("https://example.test/catalog.json"), "CATALOG_API", "unsupported plugin API blocked while reading directory", check);
        handler.Catalog = bytes;
        using var corrupt = new DistributionService(Path.Combine(root, "corrupt-download"), new Handler(bytes, [9, 9, 9, 9]));
        var corruptListing = await corrupt.ReadAsync("https://example.test/catalog.json");
        await Reject(() => corrupt.DownloadAsync(corruptListing, corruptListing.Catalog.Entries.Single()), "DOWNLOAD_HASH", "corrupt downloaded bytes cannot become installed cache", check);
        check(!Directory.EnumerateFiles(Path.Combine(root, "corrupt-download/distribution-cache")).Any(p => !p.EndsWith(".lock", StringComparison.Ordinal)), "hash failure removes untrusted content and resume state");
        var local = Path.Combine(root, "local-catalog.json");
        await File.WriteAllBytesAsync(local, JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, "Fixture", [entry with { Archive = "../escape.studioxplugin" }]), JsonStore.Options));
        try
        {
            await service.ReadAsync(local);
            check(false, "local archive escape");
        }
        catch (StudioXException) { check(true, "local catalog archive cannot escape catalog directory"); }
        handler.Catalog = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, "Fixture", [entry, entry]), JsonStore.Options);
        await Reject(() => service.ReadAsync("https://example.test/catalog.json"), "CATALOG_ENTRY", "duplicate catalog identity rejected", check);
        handler.Status = HttpStatusCode.Redirect;
        try
        {
            await service.ReadAsync("https://example.test/catalog.json");
            check(false, "redirect");
        }
        catch (HttpRequestException) { check(true, "redirect response not treated as a valid catalog"); }
    }
    private static async Task Reject(Func<Task> action, string code, string message, Action<bool, string> check)
    {
        try
        {
            await action();
            check(false, message);
        }
        catch (StudioXException error) { check(error.Code == code, message); }
    }
    private sealed class Handler(byte[] catalog, byte[] archive) : HttpMessageHandler
    {
        public byte[] Catalog { get; set; } = catalog;
        public byte[] Signature { get; set; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var data = path.EndsWith(".sig", StringComparison.Ordinal) ? Signature : path.EndsWith(".json", StringComparison.Ordinal) ? Catalog : archive;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(data) });
        }
    }
}
