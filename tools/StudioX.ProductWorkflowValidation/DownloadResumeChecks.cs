namespace StudioX.ProductWorkflowValidation;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using StudioX.Application.Distribution;
using StudioX.Foundation;

internal static class DownloadResumeChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 17)).ToArray();
        var item = new DistributionEntry("tool", "test.resume", "1.0.0", "Resume fixture", "archive.studioxtools",
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, bytes.Length * 2, "NOASSERTION", "https://example.test/source", "fixture");
        var listing = new DistributionListing(new(1, "Fixture", [item]), "https://example.test/catalog.json", "fixture", "fixture");
        var folder = Path.Combine(root, "resume-cache");
        using (var interrupted = new DistributionService(folder, new Handler(bytes) { Interrupt = true }))
        {
            try
            {
                await interrupted.DownloadAsync(listing, item);
                check(false, "interrupted transfer");
            }
            catch (IOException) { check(interrupted.DownloadState(item).SavedBytes == 131072, "network interruption preserves exact downloaded bytes"); }
        }
        var handler = new Handler(bytes);
        using (var resumed = new DistributionService(folder, handler))
        {
            var file = await resumed.DownloadAsync(listing, item);
            check(handler.Offset == 131072 && handler.IfRange == "\"fixture-v1\"" && (await File.ReadAllBytesAsync(file)).SequenceEqual(bytes), "new service instance resumes with Range and strong If-Range then verifies whole SHA-256");
            check(!Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.partial").Any(), "verified download publishes complete cache and removes partial");
            await File.WriteAllBytesAsync(file, [9, 9]);
            await resumed.DownloadAsync(listing, item);
            check(handler.Requests == 2 && handler.Offset == 0, "corrupt completed cache is recovered with a fresh verified download");
        }
        foreach (var mode in new[] { "ignored", "range", "etag", "416", "metadata" })
        {
            var data = Path.Combine(root, "resume-" + mode);
            using (var interrupted = new DistributionService(data, new Handler(bytes) { Interrupt = true }))
            {
                try
                {
                    await interrupted.DownloadAsync(listing, item);
                }
                catch (IOException) { }
            }
            var next = new Handler(bytes) { Mode = mode };
            using var service = new DistributionService(data, next);
            if (mode == "metadata")
            {
                await File.WriteAllTextAsync(Directory.EnumerateFiles(Path.Combine(data, "distribution-cache"), "*.resume.json").Single(), "{");
            }
            if (mode is "ignored" or "metadata")
            {
                var path = await service.DownloadAsync(listing, item);
                check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes) && (mode != "metadata" || next.Offset == 0), mode == "ignored"
                    ? "server ignoring Range restarts rather than appending full content" : "damaged resume metadata restarts safely");
            }
            else
            {
                await Reject(() => service.DownloadAsync(listing, item), "DOWNLOAD_RANGE", check, mode + " response rejected");
                check(service.DownloadState(item).SavedBytes == 0, mode + " invalid partial is discarded for explicit retry");
            }
        }
        var cancellationHandler = new Handler(bytes);
        using (var service = new DistributionService(Path.Combine(root, "resume-cancel"), cancellationHandler))
        {
            using var cancel = new CancellationTokenSource();
            try
            {
                await service.DownloadAsync(listing, item, new CallbackProgress(text => { if (text.StartsWith("下载 ", StringComparison.Ordinal)) { cancel.Cancel(); } }), cancel.Token);
                check(false, "cancel transfer");
            }
            catch (OperationCanceledException) { check(service.DownloadState(item).SavedBytes == 1024 * 1024, "explicit cancel retains completed writes"); }
            var required = service.SpacePlan(item, root).Sum(p => p.RequiredBytes);
            check(required == DistributionService.RequiredFreeBytes(item) - 1024 * 1024, "space preview accounts for bytes already stored while retaining install staging allowance");
            await service.DownloadAsync(listing, item);
            check(cancellationHandler.Offset == 1024 * 1024, "cancelled download resumes at durable byte boundary");
        }
        using (var service = new DistributionService(Path.Combine(root, "resume-hash-cancel"), new Handler(bytes)))
        {
            using var cancel = new CancellationTokenSource();
            try
            {
                await service.DownloadAsync(listing, item, new CallbackProgress(t => { if (t.Contains("正在校验 SHA-256", StringComparison.Ordinal)) { cancel.Cancel(); } }), cancel.Token);
            }
            catch (OperationCanceledException) { check(service.DownloadState(item).SavedBytes == bytes.Length && !service.DownloadState(item).Complete, "cancellation before hash does not publish unverified cache"); }
            await service.DownloadAsync(listing, item);
            check(service.DownloadState(item).Complete, "fully stored partial is reverified without an out-of-range request");
        }
        using (var service = new DistributionService(Path.Combine(root, "resume-short"), new Handler(bytes) { Mode = "short" }))
        {
            await Reject(() => service.DownloadAsync(listing, item), "DOWNLOAD_INCOMPLETE", check, "early EOF preserves resumable progress");
            check(service.DownloadState(item).SavedBytes == 131072, "early EOF preserves only received bytes");
        }
        using (var service = new DistributionService(Path.Combine(root, "resume-concurrent"), new Handler(bytes)))
        {
            var cache = Path.Combine(root, "resume-concurrent/distribution-cache");
            Directory.CreateDirectory(cache);
            using var held = new FileStream(Path.Combine(cache, item.Sha256 + ".studioxtools.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            await Reject(() => service.DownloadAsync(listing, item), "DOWNLOAD_BUSY", check, "competing IDE download cannot mutate shared partial");
        }
        foreach (var mode in new[] { "cdn", "insecure", "loop" })
        {
            var redirect = new RedirectHandler(bytes, mode);
            using var service = new DistributionService(Path.Combine(root, "redirect-" + mode), redirect);
            if (mode == "cdn")
            {
                var path = await service.DownloadAsync(listing, item);
                check(redirect.Requests == 2 && (await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "HTTPS archive redirect to CDN is bounded and full content remains hash verified");
            }
            else
            {
                await Reject(() => service.DownloadAsync(listing, item), mode == "loop" ? "DOWNLOAD_REDIRECT" : "CATALOG_URL", check, mode + " archive redirect blocked");
            }
        }
    }

    private static async Task Reject(Func<Task> action, string code, Action<bool, string> check, string label)
    {
        try
        {
            await action();
            check(false, label);
        }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
    private sealed class RedirectHandler(byte[] bytes, string mode) : HttpMessageHandler
    {
        public int Requests
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests++;
            if (mode == "cdn" && request.RequestUri!.Host == "cdn.example.test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(mode == "insecure" ? "http://example.test/insecure" : "https://cdn.example.test/archive");
            return Task.FromResult(response);
        }
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public bool Interrupt
        {
            get; init;
        }
        public string Mode { get; init; } = "";
        public long Offset
        {
            get; private set;
        }
        public string? IfRange
        {
            get; private set;
        }
        public int Requests
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests++;
            Offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            IfRange = request.Headers.IfRange?.ToString();
            var start = Mode == "ignored" ? 0 : Offset;
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            if (Mode == "416")
            {
                response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
            }
            response.Headers.ETag = new(Mode == "etag" ? "\"changed\"" : "\"fixture-v1\"");
            response.Content = new StreamContent(new Input(bytes[(int)start..], Interrupt, Mode == "short"));
            if (start > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(Mode == "range" ? start + 1 : start, bytes.Length - 1, bytes.Length);
            }
            return Task.FromResult(response);
        }
    }
    private sealed class Input(byte[] data, bool interrupt, bool shortRead) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => position; set => throw new NotSupportedException();
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (position >= 131072 && interrupt)
            {
                throw new IOException("Fixture: connection interrupted");
            }
            if (position >= 131072 && shortRead)
            {
                return ValueTask.FromResult(0);
            }
            var count = Math.Min(Math.Min(buffer.Length, 65536), data.Length - position);
            data.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush()
        {
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
