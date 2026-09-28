namespace StudioX.Application.Espressif;

/// <summary>提供可展示的连接状态，不包含账户、令牌、授权地址或动态注册凭据。</summary>
public sealed record EspressifDocumentationStatus(
    string Status,
    bool AuthenticationSaved,
    string? Operation,
    string Message,
    string Endpoint,
    string? LastErrorCode);
