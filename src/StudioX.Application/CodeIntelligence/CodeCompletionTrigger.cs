namespace StudioX.Application.CodeIntelligence;

/// <summary>C/C++ 自动补全的文本触发规则；不依赖候选窗口是否已经完成异步显示。</summary>
public static class CodeCompletionTrigger
{
    public static bool ShouldComplete(string text, int offset, char character)
    {
        if (offset <= 0 || offset > text.Length || text[offset - 1] != character)
        {
            return false;
        }
        if (char.IsLetter(character) || character is '_' or '.' or '>' or ':' or '"' or '<' or '/')
        {
            return true;
        }
        if (!char.IsDigit(character))
        {
            return false;
        }
        // P11、TIM2 等名称中的数字也必须发起请求；快速输入会先取消旧请求，不能等待窗口出现才允许继续。
        var start = offset - 1;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }
        return char.IsLetter(text[start]) || text[start] == '_';
    }
}
