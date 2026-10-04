namespace StudioX.Application.Editing;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;

/// <summary>个人模板与工程共享模板分开存储；保存时检查读取基线，避免多个 IDE 相互覆盖。</summary>
public sealed partial class CodeTemplateService(string dataDirectory)
{
    public const int MaximumBodyLength = 65_536;
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    public const int MaximumTemplates = 500;
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "全部", "C/C++", "C", "C++", "Python", "CMake", "Assembly", "Linker", "Devicetree", "Verilog", "JSON", "XML", "AGM Pin Map", "Text" });
    private sealed record TemplateFile(int FormatVersion, CodeTemplate[] Templates);

    public async Task<CodeTemplateLibrary> LoadAsync(string? projectDirectory, CancellationToken token = default)
    {
        var user = await LoadStoreAsync(GetPath(null, CodeTemplateScope.User), CodeTemplateScope.User, token).ConfigureAwait(false);
        var project = projectDirectory is null ? null : await LoadStoreAsync(GetPath(projectDirectory, CodeTemplateScope.Project), CodeTemplateScope.Project, token).ConfigureAwait(false);
        return new(user, project);
    }

    public async Task<IReadOnlyList<CodeTemplateEntry>> CompleteAsync(string? projectDirectory, string language, string text, int offset, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, text.Length);
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }
        var prefix = text[start..offset];
        var matches = (await LoadAsync(projectDirectory, token).ConfigureAwait(false)).Entries.Where(entry => Supports(entry.Template, language) &&
            entry.Template.Shortcut.Length > 0 && entry.Template.Shortcut.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 0 && language is "Python" or "CMake")
        {
            // 复用已有词法规则且在后台判断，避免三引号、括号注释误弹出模板或阻塞编辑器。
            if (text.Length > 1024 * 1024 || !await Task.Run(() => language == "Python" ? !PythonSyntax.Scan(text, offset, token).Suppressed : !CMakeSyntax.Read(text, offset, token).Suppressed, token).ConfigureAwait(false))
            {
                return [];
            }
        }
        return matches;
    }

    public async Task SaveAsync(string? projectDirectory, CodeTemplateScope scope, string expectedRevision, IReadOnlyList<CodeTemplate> templates, CancellationToken token = default)
    {
        if (scope == CodeTemplateScope.BuiltIn)
        {
            throw Error("内置模板请先复制为个人或工程模板。");
        }
        Validate(templates);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TemplateFile(1, templates.ToArray()), JsonStore.Options);
        if (bytes.Length > MaximumFileBytes)
        {
            throw Error("模板文件超过 4 MiB 上限。");
        }
        var path = GetPath(projectDirectory, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        path = GetPath(projectDirectory, scope);
        // 文件锁覆盖重新读取、版本比较和原子替换；锁文件保留以免删锁与另一个进程竞争。
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = await LoadStoreAsync(path, scope, token).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
        {
            throw new StudioXException("CODE_TEMPLATE_STALE", "模板文件已被其他窗口或程序修改。请关闭后重新打开模板管理，再保存。");
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public static bool Supports(CodeTemplate template, string language) => template.Language == "全部" || template.Language == language || template.Language == "C/C++" && language is "C" or "C++";

    public static void Validate(IReadOnlyList<CodeTemplate> templates)
    {
        if (templates.Count > MaximumTemplates)
        {
            throw Error("每个模板库最多保存 500 个模板。");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            if (template is null || !Guid.TryParseExact(template.Id, "N", out _) || !ids.Add(template.Id) ||
                string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 100 || template.Name.Any(char.IsControl) ||
                template.Shortcut is null || template.Shortcut.Length > 40 || template.Shortcut.Length > 0 && !Identifier().IsMatch(template.Shortcut) ||
                template.Language is null || !Languages.Contains(template.Language) || template.Description is null || template.Description.Length > 1000 ||
                string.IsNullOrEmpty(template.Body) || template.Body.Length > MaximumBodyLength || template.Body.Contains('\0'))
            {
                throw Error("模板字段无效：名称最多 100 字，缩写应为字母/下划线开头的标识符，正文最多 65,536 字符。");
            }
            if (template.Shortcut.Length > 0 && !shortcuts.Add(template.Language + ":" + template.Shortcut))
            {
                throw Error("同一模板库、同一语言不能重复使用缩写：" + template.Shortcut);
            }
            CodeTemplateExpander.Describe(template.Body);
        }
    }

    private string GetPath(string? projectDirectory, CodeTemplateScope scope) => scope switch
    {
        CodeTemplateScope.User => PathBoundary.Resolve(dataDirectory, "code-templates/templates.json"),
        CodeTemplateScope.Project when projectDirectory is not null && Directory.Exists(projectDirectory) => PathBoundary.Resolve(projectDirectory, ".studiox/code-templates.json"),
        _ => throw Error("工程模板需要先打开一个存在的工程目录。")
    };

    private static async Task<CodeTemplateStore> LoadStoreAsync(string path, CodeTemplateScope scope, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            return new(scope, path, "missing", []);
        }
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumFileBytes)
            {
                throw Error("模板文件超过 4 MiB 上限：" + path);
            }
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        TemplateFile file;
        try
        {
            file = JsonSerializer.Deserialize<TemplateFile>(bytes, JsonStore.Options) ?? throw Error("模板文件为空：" + path);
        }
        catch (JsonException ex) { throw new StudioXException("CODE_TEMPLATE_JSON", "模板 JSON 无效：" + path, ex); }
        if (file.FormatVersion != 1 || file.Templates is null)
        {
            throw Error("仅支持代码模板格式 1：" + path);
        }
        Validate(file.Templates);
        return new(scope, path, Convert.ToHexString(SHA256.HashData(bytes)), Array.AsReadOnly(file.Templates));
    }

    private static StudioXException Error(string message) => new("CODE_TEMPLATE_INVALID", message);
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
