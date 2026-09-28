namespace StudioX.Application;

using System.Diagnostics;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>查找与替换共用匹配规则；先计算完整计划，再由编辑器作为一次可撤销操作应用。</summary>
public static class TextSearchService
{
    public static IReadOnlyList<TextSearchMatch> Find(string text, TextSearchOptions options, string? replacement = null)
    {
        if (options.Pattern.Length == 0)
        {
            return [];
        }
        var pattern = options.RegularExpression ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord)
        {
            pattern = @"(?<![\p{L}\p{M}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{M}\p{N}_])";
        }
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }
        if (options.RegularExpression && options.WholeWord)
        {
            // 先验证原表达式，避免全词边界中的 ] 意外补全用户尚未闭合的字符组。
            _ = new Regex(options.Pattern, flags, TimeSpan.FromMilliseconds(150));
        }
        var expression = new Regex(pattern, flags, TimeSpan.FromMilliseconds(150));
        var clock = Stopwatch.StartNew();
        var result = new List<TextSearchMatch>();
        long outputLength = text.Length;
        for (var match = expression.Match(text); match.Success; match = match.NextMatch())
        {
            if (clock.ElapsedMilliseconds > 1000 || result.Count >= 50000)
            {
                throw new StudioXException("SEARCH_LIMIT", "匹配结果超过 50,000 处或搜索耗时过长，请缩小查找范围。");
            }
            var value = replacement is null ? "" : options.RegularExpression ? match.Result(replacement) : replacement;
            if (replacement is not null)
            {
                outputLength += value.Length - match.Length;
                if (outputLength > 4 * 1024 * 1024)
                {
                    throw new StudioXException("REPLACE_LIMIT", "替换后文本超过编辑器的 4 MiB 限制，未修改文件。");
                }
            }
            result.Add(new(match.Index, match.Length, value));
        }
        return result;
    }
}
