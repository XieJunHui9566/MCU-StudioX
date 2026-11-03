using System.Diagnostics;
using System.Text;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Application.Editing;
using StudioX.Desktop;
using StudioX.Foundation;

/// <summary>核对差异位置、全文保留、复杂度上界及实际使用的语法定义。</summary>
internal static class CodeComparisonChecks
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "固定种子仅用于可重放的差异算法测试，不生成凭据或安全标识。")]
    internal static void Run(Action<bool, string> check)
    {
        var cases = new (string Before, string After)[]
        {
            ("", ""), ("", "new\n"), ("old\n", ""), ("same\n", "same\n"),
            ("int value = 1;\n", "int value = 20;\n"),
            ("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n"),
            ("head\ntail\n", "head\ninserted\ntail\n"),
            ("head\nremoved\ntail\n", "head\ntail\n"),
            ("a\nb", "a\nb\n"), ("a\r\nb\r\n", "a\nb\n"),
            ("a\rb\rc", "a\rB\rc"),
            ("// 中文 😀\nint count = 1;\n", "// 中文 😃\nint count = 2;\n"),
            ("a\na\nb\na\nb\n", "b\na\na\nb\na\n")
        };
        foreach (var (before, after) in cases)
        {
            CheckRoundTrip(before, after);
        }
        check(true, "对照差异覆盖新增、删除、替换、重复行、CRLF/CR/LF、末尾换行和 Unicode，能精确重建结果");
        var separated = CodeComparison.Create("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n");
        check(separated.Changes.Count == 2 && separated.Changes.All(c => c.BeforeLength == 1 && c.AfterLength == 1),
            "整文件替换仍能分离两处修改并精确标记变化字符");
        var random = new Random(116028);
        for (var sample = 0; sample < 2000; sample++)
        {
            var lines = Enumerable.Range(0, random.Next(0, 35)).Select(_ => random.Next(0, 8) + "\n").ToList();
            var original = string.Concat(lines);
            for (var edit = 0; edit < 8; edit++)
            {
                var index = random.Next(0, lines.Count + 1);
                if (index < lines.Count && random.Next(2) == 0) { lines.RemoveAt(index); }
                else { lines.Insert(index, random.Next(0, 8) + "\n"); }
            }
            CheckRoundTrip(original, string.Concat(lines));
        }
        check(true, "2000 组固定种子的多行随机增删验证差异次序、边界及全部未改动内容");
        var timer = Stopwatch.StartNew();
        var largeBefore = string.Concat(Enumerable.Repeat("int before = 1;\n", 100000));
        var largeAfter = string.Concat(Enumerable.Repeat("int after = 2;\n", 100000));
        var large = CheckRoundTrip(largeBefore, largeAfter);
        check(large.IsCoarse && large.Changes.Count == 1 && timer.Elapsed < TimeSpan.FromSeconds(5),
            "大规模完全不同的文件按有界成本回退为整块标记，全文仍精确保留");
        try
        {
            CodeComparison.Create("safe", "a" + new string('\u0301', 100000));
            throw new InvalidOperationException("Unsafe review text was admitted.");
        }
        catch (StudioXException ex) when (ex.Code == "EDITOR_TEXT_COMPLEXITY")
        {
            check(true, "审阅入口共用正文安全检查，危险字符组合不能进入渲染");
        }
        var view = new CodeComparisonView();
        const string code = "#include <stdint.h>\n// preserved context\nint main(void)\n{\n    return 1;\n}\n";
        view.Show("src/main.c", code, code.Replace("return 1", "return 2", StringComparison.Ordinal));
        check(view.Before.Text == code && view.After.Text.Contains("return 2", StringComparison.Ordinal) &&
            view.Before.IsReadOnly && view.After.IsReadOnly && view.Before.ShowLineNumbers && view.After.ShowLineNumbers,
            "审阅使用带行号的只读全文，不截断上下文或改变原文");
        foreach (var editor in new[] { view.Before, view.After })
        {
            using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting!);
            var sections = Enumerable.Range(1, editor.Document.LineCount).SelectMany(i => highlighter.HighlightLine(i).Sections).ToArray();
            check(sections.Any(s => s.Color.Name == "Keyword") && sections.Any(s => s.Color.Name == "Comment") &&
                sections.Any(s => s.Color.Name == "Number"), "对照编辑器实际生成关键字、注释与数字的语法着色");
        }
        view.Show("notes.unknown", "before\n", "after\n");
        check(view.Before.SyntaxHighlighting is null && view.After.SyntaxHighlighting is null && view.Comparison.Changes.Count > 0,
            "未知后缀保留纯文本与差异高亮，不猜测语言");
        view.Show("src/main.c", "head\ntail\n", "head\ninserted\ntail\n");
        check(view.MapOffset(5, true) == 14 && view.MapOffset(14, false) == 5,
            "插入后按真实对应位置同步滚动，新增行不会让后续原文对照错位");
        view.Show("src/main.c", "old\n", "");
        check(view.After.Text.Length == 0 && view.Comparison.Changes.Single().AfterLength == 0,
            "删除全部内容保留空结果与删除位置，切换文件不残留旧正文");
    }

    private static CodeComparison CheckRoundTrip(string before, string after)
    {
        var comparison = CodeComparison.Create(before, after);
        var result = new StringBuilder();
        var a = 0;
        var b = 0;
        foreach (var change in comparison.Changes)
        {
            if (change.BeforeStart < a || change.AfterStart < b || change.BeforeLength < 0 || change.AfterLength < 0 ||
                !before.AsSpan(a, change.BeforeStart - a).SequenceEqual(after.AsSpan(b, change.AfterStart - b)))
            {
                throw new InvalidOperationException("Invalid or missing comparison range.");
            }
            result.Append(before.AsSpan(a, change.BeforeStart - a));
            result.Append(after.AsSpan(change.AfterStart, change.AfterLength));
            a = change.BeforeStart + change.BeforeLength;
            b = change.AfterStart + change.AfterLength;
        }
        result.Append(before.AsSpan(a));
        if (result.ToString() != after || !before.AsSpan(a).SequenceEqual(after.AsSpan(b)))
        {
            throw new InvalidOperationException("Comparison failed to preserve complete snapshots.");
        }
        return comparison;
    }
}
