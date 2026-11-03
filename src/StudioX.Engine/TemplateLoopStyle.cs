namespace StudioX.Engine;

using System.Text.RegularExpressions;

/// <summary>新工程的可编辑 C 入口沿用统一风格；包原件、SDK 与已有工程不改写。</summary>
public static class TemplateLoopStyle
{
    private static readonly Regex Tokens = new(
        "\"(?:\\\\[\\s\\S]|[^\"\\\\])*\"|'(?:\\\\[\\s\\S]|[^'\\\\])*'|//(?:\\\\\\r?\\n|[^\\r\\n])*|/\\*[\\s\\S]*?\\*/|(?<loop>\\bfor[ \\t]*\\([ \\t]*;[ \\t]*;[ \\t]*\\))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Normalize(string source) => Tokens.Replace(source, match => match.Groups["loop"].Success ? "while (1)" : match.Value);

    internal static async Task PrepareEntryAsync(string root, CancellationToken token)
    {
        var path = StudioX.Foundation.PathBoundary.Resolve(root, "src/main.c");
        if (!File.Exists(path))
        {
            return;
        }
        var source = await File.ReadAllTextAsync(path, token);
        var result = Normalize(source);
        if (source != result)
        {
            await File.WriteAllTextAsync(path, result, token);
        }
    }
}
