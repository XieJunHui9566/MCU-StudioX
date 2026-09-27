using System.Text;
using StudioX.Foundation;
using static StudioX.Application.AiChatProtocolRules;

namespace StudioX.Application;

/// <summary>暂存同一工具调用的字段片段；字段完整且有效后才能成为可执行请求。</summary>
internal sealed class AiStreamToolCall
{
    private readonly StringBuilder id = new();
    private readonly StringBuilder name = new();
    private readonly StringBuilder arguments = new();

    public string Name => name.ToString();

    public void AppendId(string value) => AppendLimited(id, value, 256);
    public void AppendName(string value) => AppendLimited(name, value, 128);
    public void AppendArguments(string value) => AppendLimited(arguments, value, 512 * 1024);

    public AiToolCall Build()
    {
        var idText = id.ToString();
        var nameText = name.ToString();
        if (string.IsNullOrWhiteSpace(idText) || !ValidToolName(nameText))
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用内容无效。");
        }
        return new AiToolCall(idText, nameText, arguments.ToString());
    }

    private static void AppendLimited(StringBuilder builder, string value, int maximum)
    {
        if (builder.Length + value.Length > maximum)
        {
            throw new StudioXException("AI_RESPONSE_SIZE", "AI API 工具调用超过会话上限。");
        }
        builder.Append(value);
    }
}
