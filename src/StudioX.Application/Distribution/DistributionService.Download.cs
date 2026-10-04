namespace StudioX.Application.Distribution;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class DistributionService
{
    private sealed record ResumeIdentity(string Source, long Bytes, string? ETag);

    private string CachePath(DistributionEntry entry)
    {
        if (entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit)
            || entry.DownloadBytes < 1 || entry.DownloadBytes > 8L * 1024 * 1024 * 1024)
        {
            throw new StudioXException("CATALOG_ENTRY", "下载身份或大小无效。");
        }
        var extension = entry.Kind switch
        {
            "tool" => ToolchainArchiveFormat.CacheExtension(entry.Archive),
            "plugin" => ".studioxplugin",
            "component" => ".studioxcomponent",
            _ => throw new StudioXException("CATALOG_ENTRY", "不支持的归档类型。")
        };
        return PathBoundary.Resolve(cache, entry.Sha256.ToUpperInvariant() + extension);
    }

    public DistributionDownloadState DownloadState(DistributionEntry entry)
    {
        var path = CachePath(entry);
        var complete = File.Exists(path) && new FileInfo(path).Length == entry.DownloadBytes;
        var partial = PathBoundary.Resolve(cache, Path.GetFileName(path) + ".partial");
        var saved = complete ? entry.DownloadBytes : File.Exists(partial) ? Math.Min(new FileInfo(partial).Length, entry.DownloadBytes) : 0;
        return new(saved, entry.DownloadBytes, complete);
    }

    /// <summary>按内容哈希保存进度；取消和网络故障保留文件，完整校验通过后才发布缓存。</summary>
    public async Task<string> DownloadAsync(DistributionListing listing, DistributionEntry entry, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!listing.Catalog.Entries.Contains(entry))
        {
            throw new StudioXException("CATALOG_SELECTION", "条目不属于当前目录快照。");
        }
        Directory.CreateDirectory(cache);
        var destination = CachePath(entry);
        // OS 文件锁覆盖多个 IDE 实例；锁文件常驻，避免删除锁路径造成另一个实例失去互斥。
        var name = Path.GetFileName(destination);
        await using var lease = AcquireDownload(PathBoundary.Resolve(cache, name + ".lock"));
        var partial = PathBoundary.Resolve(cache, name + ".partial");
        var metadata = PathBoundary.Resolve(cache, name + ".resume.json");
        void Discard()
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
            if (File.Exists(metadata))
            {
                File.Delete(metadata);
            }
        }
        if (File.Exists(destination))
        {
            try
            {
                await VerifyAsync(destination, entry, token);
                progress?.Report("完整缓存校验通过。");
                return destination;
            }
            catch (StudioXException error) when (error.Code == "DOWNLOAD_HASH")
            {
                File.Delete(destination);
                progress?.Report("缓存校验失败，重新获取归档。");
            }
        }
        var online = Uri.TryCreate(listing.Source, UriKind.Absolute, out var baseUri) && baseUri.Scheme == "https";
        var uri = online ? new Uri(baseUri!, entry.Archive) : Uri.TryCreate(entry.Archive, UriKind.Absolute, out var absolute) && absolute.Scheme == "https" ? absolute : null;
        var sourcePath = uri?.AbsoluteUri ?? PathBoundary.Resolve(Path.GetDirectoryName(listing.Source)!, entry.Archive);
        if (uri is not null)
        {
            ValidateUrl(uri);
        }
        ResumeIdentity? previous = null;
        if (File.Exists(metadata) && new FileInfo(metadata).Length <= 16 * 1024)
        {
            try
            {
                previous = await JsonStore.ReadAsync<ResumeIdentity>(metadata, token);
            }
            catch (JsonException) { progress?.Report("续传记录损坏，将重新下载。"); }
            catch (StudioXException error) when (error.Code == "JSON_EMPTY") { progress?.Report("续传记录为空，将重新下载。"); }
        }
        if (previous?.Source != sourcePath || previous.Bytes != entry.DownloadBytes)
        {
            Discard();
        }
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > entry.DownloadBytes)
        {
            Discard();
            offset = 0;
        }
        HttpResponseMessage? response = null;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            // 已写完但取消在哈希校验阶段时，下一次直接复核，不请求超出文件尾的 Range。
            if (offset < entry.DownloadBytes)
            {
                Stream input;
                string? etag = null;
                if (uri is not null)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(90));
                    response = await SendArchiveAsync(uri, offset, previous?.ETag, idle.Token);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (offset == 0 || range is null || range.Unit != "bytes" || range.From != offset
                            || range.To != entry.DownloadBytes - 1 || range.Length != entry.DownloadBytes)
                        {
                            throw new StudioXException("DOWNLOAD_RANGE", "服务器续传范围与已保存字节或目录大小不一致，请重新下载。");
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (offset > 0)
                        {
                            progress?.Report("服务器返回完整文件，重新下载并校验。");
                        }
                        offset = 0;
                    }
                    else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        throw new StudioXException("DOWNLOAD_RANGE", "服务器拒绝续传范围，请重新下载。");
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new StudioXException("DOWNLOAD_ENCODING", "服务器返回了压缩传输，无法核对归档字节范围。");
                    }
                    if (response.Content.Headers.ContentLength is { } length && length != entry.DownloadBytes - offset)
                    {
                        throw new StudioXException("DOWNLOAD_SIZE", "下载长度与目录不一致。");
                    }
                    etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                    if (offset > 0 && previous?.ETag is { } expected && etag != expected)
                    {
                        throw new StudioXException("DOWNLOAD_RANGE", "续传响应的文件标识已经变化，请重新下载。");
                    }
                    input = await response.Content.ReadAsStreamAsync(token);
                }
                else
                {
                    var local = File.OpenRead(sourcePath);
                    if (local.Length != entry.DownloadBytes)
                    {
                        local.Dispose();
                        throw new StudioXException("DOWNLOAD_SIZE", "本地归档长度与目录不一致。");
                    }
                    // 离线文件可能被替换，重新复制完整内容并核验，避免混合两个版本。
                    offset = 0;
                    input = local;
                }
                await using (input)
                {
                    await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true))
                    {
                        await JsonStore.WriteAsync(metadata, new ResumeIdentity(sourcePath, entry.DownloadBytes, etag), token);
                        progress?.Report(offset > 0 ? $"继续下载：{offset:N0}/{entry.DownloadBytes:N0} 字节" : $"开始下载：0/{entry.DownloadBytes:N0} 字节");
                        var buffer = new byte[64 * 1024];
                        long received = offset, reported = offset;
                        while (true)
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(90));
                            var count = await input.ReadAsync(buffer, idle.Token);
                            idle.CancelAfter(Timeout.InfiniteTimeSpan);
                            if (count == 0)
                            {
                                break;
                            }
                            received += count;
                            if (received > entry.DownloadBytes)
                            {
                                throw new StudioXException("DOWNLOAD_SIZE", "下载超过目录声明的长度。");
                            }
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                            if (received - reported >= 1024 * 1024 || received == entry.DownloadBytes)
                            {
                                // 发布进度前落盘，进程被终止后续传范围只包含已持久保存的字节。
                                await output.FlushAsync(token);
                                output.Flush(flushToDisk: true);
                                progress?.Report($"下载 {received:N0}/{entry.DownloadBytes:N0} 字节");
                                reported = received;
                            }
                        }
                        if (received != entry.DownloadBytes)
                        {
                            throw new StudioXException("DOWNLOAD_INCOMPLETE", $"下载中断，已保存 {received:N0}/{entry.DownloadBytes:N0} 字节。可再次点击继续下载。");
                        }
                    }
                }
            }
            progress?.Report("归档已到齐，正在校验 SHA-256…");
            await VerifyAsync(partial, entry, token);
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            if (File.Exists(metadata))
            {
                File.Delete(metadata);
            }
            progress?.Report("归档校验通过，可预览安装。");
            return destination;
        }
        catch (StudioXException error) when (error.Code is "DOWNLOAD_HASH" or "DOWNLOAD_SIZE" or "DOWNLOAD_RANGE" or "DOWNLOAD_ENCODING")
        {
            Discard();
            throw;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            progress?.Report("网络等待超时；下载进度保留，可稍后继续。");
            throw new StudioXException("DOWNLOAD_TIMEOUT", "网络等待超过 90 秒；下载进度保留，可稍后继续。", error);
        }
        catch (OperationCanceledException)
        {
            progress?.Report("已取消；下载进度保留，可稍后继续。");
            throw;
        }
        finally { response?.Dispose(); }
    }

    private static FileStream AcquireDownload(string path)
    {
        try
        {
            return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) { throw new StudioXException("DOWNLOAD_BUSY", "此归档正在由另一个下载操作使用，请等待完成后重试。", error); }
    }

    private async Task<HttpResponseMessage> SendArchiveAsync(Uri uri, long offset, string? etag, CancellationToken token)
    {
        // 发布归档常跳转到 CDN；只跟随有限次数的 HTTPS 跳转，目录读取仍不自动跳转。
        for (var redirects = 0; ; redirects++)
        {
            ValidateUrl(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                }
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= 5)
            {
                throw new StudioXException("DOWNLOAD_REDIRECT", "归档跳转缺少地址或超过 5 次，请检查分发来源。");
            }
            uri = new Uri(uri, location);
        }
    }
}
