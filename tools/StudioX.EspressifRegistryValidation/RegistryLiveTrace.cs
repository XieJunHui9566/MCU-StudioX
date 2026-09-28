namespace StudioX.EspressifRegistryValidation;

using System.Text.Json;

/// <summary>只记录本验证固定公开查询的匿名 HTTP 请求，用于核实官方 CDN 互操作问题。</summary>
internal sealed class RegistryLiveTrace(string directory) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    private int index;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref index);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        var errorBody = response.IsSuccessStatusCode || response.Content is null ? null :
            await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "anonymous-live-request-" + number + ".json"),
            JsonSerializer.Serialize(new
            {
                endpoint = request.RequestUri?.AbsoluteUri,
                method = request.Method.Method,
                version = request.Version.ToString(),
                headers = request.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray()),
                contentHeaders = request.Content?.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray()),
                body,
                status = (int)response.StatusCode,
                responseHeaders = response.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray()),
                errorBody
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return response;
    }
}
