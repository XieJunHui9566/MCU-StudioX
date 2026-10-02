namespace StudioX.Application.Help;

using System.Reflection;
using System.Text.Json;

/// <summary>读取随应用分发的帮助，按标题、关键词和正文检索；无需网络或工程。</summary>
public sealed class HelpContentService
{
    private sealed record Entry(string Id, string Category, string Title, string Summary, string[] Keywords, string[] Related);
    private static readonly Lazy<IReadOnlyList<HelpArticle>> content = new(Load);
    public IReadOnlyList<HelpArticle> Articles => content.Value;
    public IReadOnlyList<string> Categories => Articles.Select(article => article.Category).Distinct().ToArray();
    public HelpArticle Get(string id) => Articles.FirstOrDefault(article => article.Id == id)
        ?? throw new ArgumentException("帮助主题不存在：" + id, nameof(id));

    public string DiagnosticTopic(string diagnostic)
    {
        bool Has(params string[] terms) => terms.Any(term => diagnostic.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (Has("COMPONENT_")) return "components";
        if (Has("CATALOG_", "DOWNLOAD_HASH", "DOWNLOAD_SIZE", "INSTALL_SPACE", "PLUGIN_ROLLBACK")) return "distribution";
        if (Has("BUILD_HISTORY_", "BUILD_COMPARE_")) return "build-comparison";
        if (Has("FAULT_", "HardFault", "Core Dump")) return "fault-analysis";
        if (Has("XTENSA_GNU_CONFIG", "dynconfig", "head-ref", "idf_py_", "ESPRESSIF_")) return "esp-idf-errors";
        if (Has("TOOLS_", "TOOLSET_", "TOOL_MISSING", "TOOL_HASH", "TOOL_RESOURCE", "TOOL_EXECUTE", "TOOLCHAIN_LOCK", "LANGUAGE_MISSING", "工具集缺失")) return "tool-environment";
        if (Has("HEALTH_CACHE_", "CMAKE_HOME_DIRECTORY", "CMakeCache.txt directory")) return "build-errors";
        if (Has("EDITOR_FILE_CHANGED", "WORKSPACE_EDIT_STALE", "RENAME_VERSION", "FIX_VERSION")) return "recovery";
        if (Has("port is busy", "串口", "COM 端口")) return "serial-errors";
        if (Has("undefined reference", "multiple definition", "overflowed", "file not found", "No such file", "CMake Error")) return "build-errors";
        if (Has("调试连接", "target not examined", "unable to connect", "breakpoint", "ST-Link", "WCH-Link", "J-Link", "CMSIS-DAP")) return "debug-errors";
        return "troubleshooting";
    }

    public IReadOnlyList<HelpArticle> Search(string query, string? category = null)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Articles.Where(article => category is null || article.Category == category)
            .Select(article => (Article: article, Scores: terms.Select(term => Score(article, term)).ToArray()))
            .Where(result => result.Scores.All(score => score > 0))
            .OrderByDescending(result => result.Scores.Sum())
            .Select(result => result.Article).ToArray();
    }

    private static int Score(HelpArticle article, string term)
    {
        bool Has(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
        return (Has(article.Title) ? 20 : 0) + (article.Keywords.Any(Has) ? 10 : 0)
            + (Has(article.Summary) ? 5 : 0) + (Has(article.Category) ? 3 : 0) + (Has(article.Markdown) ? 1 : 0);
    }

    private static IReadOnlyList<HelpArticle> Load()
    {
        var assembly = typeof(HelpContentService).Assembly;
        const string prefix = "StudioX.Application.Help.";
        using var index = assembly.GetManifestResourceStream(prefix + "index.json")
            ?? throw new InvalidOperationException("离线帮助索引缺失。");
        var entries = JsonSerializer.Deserialize<Entry[]>(index, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("离线帮助索引为空。");
        var articles = entries.Select(entry => new HelpArticle(entry.Id, entry.Category, entry.Title, entry.Summary,
            entry.Keywords, entry.Related, Read(assembly, prefix + "Articles." + entry.Id + ".md"))).ToArray();
        if (articles.Length == 0 || articles.Select(article => article.Id).Distinct().Count() != articles.Length)
            throw new InvalidOperationException("离线帮助主题为空或 ID 重复。");
        foreach (var article in articles)
        {
            if (!article.Markdown.StartsWith("# " + article.Title + "\n", StringComparison.Ordinal)
                || article.Related.Any(id => !articles.Any(other => other.Id == id)))
                throw new InvalidOperationException("离线帮助标题或关联主题无效：" + article.Id);
        }
        return Array.AsReadOnly(articles);
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("离线帮助正文缺失：" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
