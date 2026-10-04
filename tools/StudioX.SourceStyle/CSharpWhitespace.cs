namespace StudioX.SourceStyle;

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>核对 SDK 排版结果；保护原始字面量，并在隔离副本上完成只读格式检查。</summary>
internal static class CSharpWhitespace
{
    internal static int Finish(string root, string baseline, string formatted, bool write)
    {
        var files = Directory.EnumerateFiles(baseline, "*.cs", SearchOption.AllDirectories).ToArray();
        var changes = new List<(string Path, string Before, string After)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(baseline, file);
            var original = File.ReadAllText(file);
            var result = PreserveLiterals(original, File.ReadAllText(Path.Combine(formatted, relative)));
            var target = Path.Combine(root, relative);
            if (File.ReadAllText(target) != original)
            {
                throw new InvalidOperationException("排版期间源码发生变化，拒绝覆盖：" + relative);
            }
            if (original != result)
            {
                changes.Add((target, original, result));
            }
        }
        foreach (var (path, original, result) in changes)
        {
            if (write)
            {
                if (File.ReadAllText(path) != original)
                {
                    throw new InvalidOperationException("写回前源码发生变化，拒绝覆盖：" + path);
                }
                File.WriteAllText(path, result, new UTF8Encoding(false));
            }
            Console.WriteLine(Path.GetRelativePath(root, path));
        }
        Console.WriteLine($"C# whitespace: {files.Length} files verified, {changes.Count} changes, write={write}; literal tokens preserved.");
        return !write && changes.Count > 0 ? 1 : 0;
    }

    internal static string PreserveLiterals(string original, string formatted)
    {
        var before = Parse(original);
        var after = Parse(formatted);
        var beforeTokens = before.DescendantTokens().ToArray();
        var afterTokens = after.DescendantTokens().ToArray();
        if (!beforeTokens.Select(token => token.RawKind).SequenceEqual(afterTokens.Select(token => token.RawKind)))
        {
            throw new InvalidOperationException("SDK 排版改变了 C# token 结构，已停止写入。");
        }
        var replacements = new List<(int Start, int Length, string Text)>();
        var oldInterpolations = TopInterpolations(before);
        var newInterpolations = TopInterpolations(after);
        if (oldInterpolations.Length != newInterpolations.Length)
        {
            throw new InvalidOperationException("SDK 排版改变了插值字符串结构。");
        }
        for (var index = 0; index < oldInterpolations.Length; index++)
        {
            var oldText = oldInterpolations[index].ToString();
            if (oldText != newInterpolations[index].ToString())
            {
                var span = newInterpolations[index].Span;
                replacements.Add((span.Start, span.Length, oldText));
            }
        }
        for (var index = 0; index < beforeTokens.Length; index++)
        {
            var oldToken = beforeTokens[index];
            var newToken = afterTokens[index];
            if (oldToken.Text == newToken.Text ||
                oldToken.Parent!.AncestorsAndSelf().OfType<InterpolatedStringExpressionSyntax>().Any())
            {
                continue;
            }
            if (oldToken.Parent is not LiteralExpressionSyntax)
            {
                throw new InvalidOperationException("SDK 排版改变了非字面量 token，已停止写入。");
            }
            // 多行原始字符串的换行属于值；不能跟随文件的 LF 规则改写成另一份生成文本。
            replacements.Add((newToken.SpanStart, newToken.Span.Length, oldToken.Text));
        }
        foreach (var (start, length, text) in replacements.OrderByDescending(item => item.Start))
        {
            formatted = formatted.Remove(start, length).Insert(start, text);
        }
        var restored = Parse(formatted);
        if (!beforeTokens.Select(token => token.Text).SequenceEqual(restored.DescendantTokens().Select(token => token.Text)))
        {
            throw new InvalidOperationException("排版后 C# 字面量或可执行 token 不一致，已停止写入。");
        }
        return formatted;
    }

    private static SyntaxNode Parse(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidOperationException("C# 排版前后必须具有有效语法。");
        }
        return syntax;
    }

    private static InterpolatedStringExpressionSyntax[] TopInterpolations(SyntaxNode syntax) =>
        syntax.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>()
            .Where(node => !node.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Any()).ToArray();
}
