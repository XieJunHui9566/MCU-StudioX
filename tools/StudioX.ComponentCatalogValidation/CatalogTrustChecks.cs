namespace StudioX.ComponentCatalogValidation;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application.Distribution;
using StudioX.Foundation;

internal static class CatalogTrustChecks
{
    internal static async Task RunAsync(string output, string catalogFile, bool online, Action<bool, string> check)
    {
        var catalog = await File.ReadAllBytesAsync(catalogFile);
        var signature = await File.ReadAllBytesAsync(catalogFile + ".sig");
        var handler = new Handler(catalog, signature);
        using var service = new DistributionService(Path.Combine(output, "trust"), handler);
        check(handler.Requests.Count == 0, "constructing catalog service does not access network or create cache");
        var listing = await service.ReadTrustedAsync();
        check(listing.BuiltInTrusted && listing.Verification.Contains(TrustedDevelopmentCatalog.KeySha256, StringComparison.OrdinalIgnoreCase)
            && listing.Source == TrustedDevelopmentCatalog.Source, "published catalog verifies with assembly-embedded pinned public key");
        check(handler.Requests.Count == 2 && handler.Requests.All(uri => uri.AbsoluteUri is TrustedDevelopmentCatalog.Source or TrustedDevelopmentCatalog.Source + ".sig"),
            "trusted entry requests only catalog and signature, never downloads a public key");
        using var wrongKey = RSA.Create(2048);
        var keyFile = Path.Combine(output, "unrelated.pub.pem");
        await File.WriteAllTextAsync(keyFile, wrongKey.ExportSubjectPublicKeyInfoPem());
        check((await service.ReadAsync(TrustedDevelopmentCatalog.Source, keyFile)).BuiltInTrusted, "custom key selection cannot replace built-in catalog trust anchor");
        handler.Signature = [];
        await Reject(() => service.ReadAsync(TrustedDevelopmentCatalog.Source), "CATALOG_SIGNATURE", "missing signature cannot downgrade built-in source to unsigned", check);
        handler.Signature = Encoding.ASCII.GetBytes("invalid base64!");
        await Reject(() => service.ReadTrustedAsync(), "CATALOG_SIGNATURE", "malformed trusted signature is rejected with stable diagnostic", check);
        handler.Signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(wrongKey.SignData(catalog, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        await Reject(() => service.ReadTrustedAsync(), "CATALOG_SIGNATURE", "self-signed replacement catalog cannot acquire built-in trust", check);
        handler.Signature = signature;
        var description = JsonSerializer.Deserialize<DistributionCatalog>(catalog, JsonStore.Options)!;
        handler.Catalog = JsonSerializer.SerializeToUtf8Bytes(description with { Publisher = "Altered publisher" }, JsonStore.Options);
        await Reject(() => service.ReadTrustedAsync(), "CATALOG_SIGNATURE", "catalog byte changes invalidate pinned publisher signature", check);
        handler.Catalog = catalog;
        var custom = await service.ReadAsync("https://example.test/catalog.json");
        check(!custom.BuiltInTrusted && custom.Verification.Contains("未验证签名"), "custom catalog never inherits built-in trust from matching content");
        handler.Status = HttpStatusCode.Redirect;
        try { await service.ReadTrustedAsync(); throw new InvalidOperationException("redirect accepted"); }
        catch (HttpRequestException) { check(true, "trusted catalog refuses redirect downgrade"); }
        handler.Status = HttpStatusCode.OK;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ReadTrustedAsync(cancelled.Token); throw new InvalidOperationException("cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "trusted catalog read respects cancellation"); }
        if (online)
        {
            using var remote = new DistributionService(Path.Combine(output, "online"));
            var actual = await remote.ReadTrustedAsync();
            check(actual.BuiltInTrusted && actual.Catalog.Entries.Any(e => e.Id == "stc.sdcc" && e.Version == "1.0.1"),
                "actual public HTTPS catalog verifies without user key file");
            await JsonStore.WriteAsync(Path.Combine(output, "trusted-online.json"), actual);
        }
    }
    private static async Task Reject(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try { await action(); throw new InvalidOperationException(label); }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
    private sealed class Handler(byte[] catalog, byte[] signature) : HttpMessageHandler
    {
        internal byte[] Catalog { get; set; } = catalog;
        internal byte[] Signature { get; set; } = signature;
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? Signature : Catalog) });
        }
    }
}
