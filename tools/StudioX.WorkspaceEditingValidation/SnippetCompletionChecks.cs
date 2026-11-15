namespace StudioX.WorkspaceEditingValidation;

using System.Text.Json;
using StudioX.Application.CodeIntelligence;

internal static class SnippetCompletionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var snippet = CodeSnippet.Parse("for (${1:init}; ${2:condition}; ${3:step}) {\n$0\n}")!;
        check(snippet.Text == "for (init; condition; step) {\n\n}" && snippet.Stops.Select(stop => stop.Index).SequenceEqual([1, 2, 3, 0]), "LSP placeholders are removed and ordered with final cursor last");
        var prepared = snippet.PrepareInsertion("    ", "\r\n", "    ", true);
        check(prepared.Text == "for (init; condition; step) {\r\n        \r\n    }" && prepared.Stops.Take(3).Select(stop => prepared.Text.Substring(stop.Offset, stop.Length)).SequenceEqual(["init", "condition", "step"]), "snippet reindent preserves all editable ranges and CRLF");
        check(prepared.Stops[^1].Offset == prepared.Text.IndexOf("\r\n    }", StringComparison.Ordinal), "final cursor is inside the indented statement body");
        var escaped = CodeSnippet.Parse("😀 ${2:a\\}b\\$c\\\\d} ${1:先} $0")!;
        check(escaped.Text == "😀 a}b$c\\d 先 " && escaped.Stops[0].Index == 1 && escaped.Text.Substring(escaped.Stops[1].Offset, escaped.Stops[1].Length) == "a}b$c\\d", "escaped defaults, Unicode UTF-16 and numeric navigation order are retained");
        check(CodeSnippet.Parse("return;") is { Text: "return;", Stops.Count: 1 }, "snippet-format plain text has an implicit final cursor");
        try
        {
            CodeSnippet.Parse(string.Concat(Enumerable.Repeat("{\n", 2000)))!.PrepareInsertion("", "\n", "    ", true);
            throw new InvalidOperationException("Excessive indentation expansion was accepted.");
        }
        catch (InvalidDataException)
        {
            check(true, "nested indentation expansion is bounded before document mutation");
        }
        foreach (var invalid in new[] { "${1:missing", "$variable", "${1|a,b|}", "${1:${2:nested}}", "${1:value} $1", "$99999999999999999999999", new string('x', 65_537) })
        {
            check(CodeSnippet.Parse(invalid) is null, "unsupported or excessive snippet is rejected before insertion: " + invalid[..Math.Min(35, invalid.Length)]);
        }
    }

    internal static async Task RunAsync(CodeIntelligenceService service, string project, string output, Action<bool, string> check)
    {
        var captured = new List<object>();
        foreach (var path in new[] { "src/main.c", "src/probe.cpp" })
        {
            if (path.EndsWith(".cpp", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(Path.Combine(project, path), "void probe(void) {}\n");
            }
            foreach (var (prefix, expected) in new[] { ("wh", "while"), ("if", "if"), ("sw", "switch"), ("fo", "for"), ("do", "do"), ("ret", "return") })
            {
                var text = "void probe(void)\n{\n    " + prefix + "\n}\n";
                var offset = text.IndexOf(prefix + "\n", StringComparison.Ordinal) + prefix.Length;
                // 先等待此隔离翻译单元的 AST；不把冷启动的标识符回退当作语义结果。
                await service.DocumentSymbolsAsync(path, text);
                var suggestions = await service.CompleteAsync(path, text, offset);
                var match = suggestions.FirstOrDefault(item => item.FilterText == expected);
                check(match is { Snippet: not null } && match.InsertText.StartsWith(expected, StringComparison.Ordinal) && !match.InsertText.Contains("${", StringComparison.Ordinal), path + " real clangd supplies " + expected + " with parsed snippet");
                captured.Add(new
                {
                    path,
                    prefix,
                    match
                });
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "c-completion.json"), JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true }));
    }
}
