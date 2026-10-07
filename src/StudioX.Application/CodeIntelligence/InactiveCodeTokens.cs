namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;

/// <summary>读取 clangd 的完整语义标记；其 comment 类型用于未启用的预处理分支。</summary>
public static class InactiveCodeTokens
{
    public static IReadOnlyList<CodeRange> Decode(JsonElement response, string text, int inactiveType, int tokenTypeCount)
    {
        if (response.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        var data = response.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() % 5 != 0 || data.GetArrayLength() > 250000)
        {
            throw new JsonException("未启用代码标记格式无效或超出 50,000 个标记上限。");
        }
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        var values = data.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var regions = new List<CodeRange>();
        var line = 0;
        var character = 0;
        var previousEnd = 0;
        for (var i = 0; i < values.Length; i += 5)
        {
            var deltaLine = values[i];
            var deltaCharacter = values[i + 1];
            var length = values[i + 2];
            var kind = values[i + 3];
            if (deltaLine < 0 || deltaCharacter < 0 || length < 0 || length == 0 && kind != inactiveType ||
                kind < 0 || kind >= tokenTypeCount || values[i + 4] < 0)
            {
                throw new JsonException("未启用代码标记包含负坐标、空范围或未知类型。");
            }
            line = checked(line + deltaLine);
            character = checked(deltaLine == 0 ? character + deltaCharacter : deltaCharacter);
            var endCharacter = checked(character + length);
            if (line >= starts.Count)
            {
                throw new JsonException("未启用代码标记超出文档行数。");
            }
            var start = starts[line];
            var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            if (end > start && text[end - 1] == '\r')
            {
                // clangd 的整行 InactiveCode 标记包含 CRLF 中的 CR；显示范围只去掉这个已核实的换行字符。
                if (kind == inactiveType && endCharacter == end - start)
                {
                    endCharacter--;
                }
                end--;
            }
            if (endCharacter > end - start)
            {
                throw new JsonException($"未启用代码标记超出当前行的 UTF-16 范围：{line}:{character}+{length}，行长 {end - start}。");
            }
            if (kind != inactiveType || endCharacter == character)
            {
                continue;
            }
            if (start + character < previousEnd || regions.Count >= 5000)
            {
                throw new JsonException("未启用代码范围重叠或超出 5,000 行上限。");
            }
            regions.Add(new(new(line, character), new(line, endCharacter)));
            previousEnd = start + endCharacter;
        }
        return regions;
    }
}
