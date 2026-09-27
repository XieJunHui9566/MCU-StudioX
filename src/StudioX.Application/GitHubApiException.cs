namespace StudioX.Application;

using System.Net;

/// <summary>GitHub 拒绝操作时保留状态码；消息仅由安全的服务端 message 字段生成。</summary>
public sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
