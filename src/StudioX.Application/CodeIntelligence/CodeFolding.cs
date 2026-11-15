namespace StudioX.Application.CodeIntelligence;

/// <summary>复用注释、字符串及括号扫描；不依赖构建结果或语言服务器。</summary>
public static class CodeFolding
{
    public static bool Supports(string language) => language is "C" or "C++" or "JSON" or "Linker";

    public static CodeFoldingRange[] Find(string text, string language, CancellationToken token = default)
    {
        if (!Supports(language))
        {
            return [];
        }
        var result = new List<CodeFoldingRange>();
        var opened = new Dictionary<int, (int Offset, int Line)>();
        var line = 0;
        var nextLine = text.IndexOf('\n');
        foreach (var bracket in BracketPairs.Find(text, language, token))
        {
            token.ThrowIfCancellationRequested();
            while (nextLine >= 0 && nextLine < bracket.Offset)
            {
                line++;
                nextLine = text.IndexOf('\n', nextLine + 1);
            }
            var character = text[bracket.Offset];
            if (character == '{' || language == "JSON" && character == '[')
            {
                opened[bracket.Depth] = (bracket.Offset, line);
            }
            else if ((character == '}' || language == "JSON" && character == ']') && opened.Remove(bracket.Depth, out var start) && line > start.Line)
            {
                result.Add(new(start.Offset + 1, bracket.Offset));
            }
        }
        return result.OrderBy(range => range.StartOffset).ToArray();
    }
}
