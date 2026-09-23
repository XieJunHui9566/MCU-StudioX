namespace StudioX.Foundation;

/// <summary>稳定错误码供界面、日志和扩展使用；消息可翻译，代码不可随文案变化。</summary>
public sealed class StudioXException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
