namespace StudioX.Application.CodeIntelligence;

using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

public sealed class VerilogAssistanceService(ToolsetCatalog catalog)
{
    private readonly HdlEditorAnalysis analysis = new(catalog);
    private readonly object sync = new();
    private (string Root, string Path, long Stamp, HdlEditorAnalysis.Snapshot Snapshot)? indexed;

    private static readonly string[] Directives = ["define", "include", "ifdef", "ifndef", "elsif", "else", "endif", "undef", "timescale", "default_nettype", "resetall"];
    private static readonly string[] SystemTasks = ["display", "write", "strobe", "monitor", "stop", "finish", "fatal", "error", "warning", "info", "time", "realtime", "clog2", "bits", "signed", "unsigned", "readmemh", "readmemb", "dumpfile", "dumpvars", "random", "urandom"];

    public static bool IsCodeContext(string text, int offset) =>
        text.Length <= 1_048_576 && !VerilogDocument.Read(text, CancellationToken.None).InCommentOrString(offset);

    public static bool Supports(string path) => HdlEditorAnalysis.Supports(path);

    public Task<(CodeDiagnosticBatch[] Batches, string Status)> DiagnoseAsync(string root, string path, string text,
        CodeDocumentSnapshot[] documents, CancellationToken token) => Task.Run(async () =>
    {
        var snapshot = await analysis.CaptureAsync(root, path, text, Drafts(documents), token);
        var result = await analysis.AnalyzeAsync(snapshot, path, token);
        var reportedPaths = result.Findings.Select(finding => finding.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        reportedPaths.Add(Normalize(path));
        var open = Drafts(documents);
        var batches = snapshot.Files.Where(file => reportedPaths.Contains(file.Key) || open.ContainsKey(file.Key))
            .Select(file => new CodeDiagnosticBatch(root, file.Key, file.Value, 0, result.Findings
                .Where(finding => finding.Path.Equals(file.Key, StringComparison.OrdinalIgnoreCase))
                .Select(finding => MakeDiagnostic(file.Value, finding)).OfType<CodeDiagnostic>().ToArray())
            {
                IsComplete = result.Complete
            })
            .ToArray();
        return (batches, result.Status);
    }, token);

    private static CodeDiagnostic? MakeDiagnostic(string text, HdlEditorAnalysis.Finding finding)
    {
        int line = finding.Line - 1;
        if (line < 0 || line > CodePositions.FromOffset(text, text.Length).Line)
        {
            return null;
        }
        int start = CodePositions.ToOffset(text, new(line, 0));
        int end = text.IndexOf('\n', start);
        if (end < 0)
        {
            end = text.Length;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        return new(Range(text, start, end), finding.Warning ? 2 : 1, finding.Message, "Icarus Verilog", "");
    }

    public Task<IReadOnlyList<CodeSuggestion>> CompleteAsync(string? root, string path, string text, int offset,
        CodeDocumentSnapshot[] documents, CancellationToken token) => Task.Run<IReadOnlyList<CodeSuggestion>>(async () =>
    {
        if (text.Length > 1_048_576)
        {
            return [];
        }
        var document = VerilogDocument.Read(text, token);
        if (document.InCommentOrString(offset))
        {
            return [];
        }
        int start = offset, end = offset;
        while (start > 0 && Identifier(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && Identifier(text[end]))
        {
            end++;
        }
        char marker = start > 0 ? text[start - 1] : '\0';
        if (char.IsAsciiDigit(text.ElementAtOrDefault(start)) ||
            document.Tokens.Any(t => t.Offset < offset && t.End >= offset && char.IsAsciiDigit(t.Text[0])))
        {
            return [];
        }
        var prefix = text[start..offset];
        var range = Range(text, start, end);
        var suggestions = new List<CodeSuggestion>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (marker == '$')

        {
            foreach (var name in SystemTasks)
            {
                Add(name, "系统任务 / 函数", 3, "$" + name);
            }
        }
        else
        {
            if (marker == '`')

            {
                foreach (var name in Directives)
                {
                    Add(name, "编译指令", 14, "`" + name);
                }
            }
            else if (marker != '.')
            {
                foreach (var symbol in document.Symbols.Where(s => s.Kind != 5 && s.Role != "宏" && Visible(s, offset)))

                {
                    Add(symbol.Name.Text, symbol.Role, symbol.Kind == 12 ? 3 : 6);
                }
            }

            var files = await GetFilesAsync(root, path, text, documents, token);
            if (marker == '.' && document.NamedConnections(start - 1) is { } binding)
            {
                foreach (var (_, _, parsed) in ParseFiles(files, token))

                {
                    foreach (var module in parsed.Symbols.Where(s => s.Kind == 5 && s.Name.Text == binding.Module))

                    {
                        foreach (var member in module.Members.Where(s => (binding.Parameters ? s.Role == "parameter" : s.Kind == 8) && !binding.Used.Contains(s.Name.Text)))

                        {
                            Add(member.Name.Text, member.Role + " · " + binding.Module, binding.Parameters ? 21 : 5);
                        }
                    }
                }
            }
            else if (marker != '.')
            {
                foreach (var (relative, _, parsed) in ParseFiles(files, token))

                {
                    foreach (var symbol in parsed.Symbols.Where(s => marker == '`' ? s.Role == "宏" : s.Kind == 5))

                    {
                        Add(symbol.Name.Text, symbol.Role + " · " + relative, symbol.Kind == 5 ? 7 : 21,
                                                    marker == '`' ? "`" + symbol.Name.Text : null);
                    }
                }
                if (marker == '`')

                {
                    foreach (var define in files.Defines)

                    {
                        Add(define.Split('=')[0], "工程宏", 21, "`" + define.Split('=')[0]);
                    }
                }
                else
                {
                    foreach (var keyword in VerilogDocument.Keywords.Split(' '))

                    {
                        Add(keyword, "Verilog 关键字", 14);
                    }
                    Add("模块定义", "module … endmodule", 15, filter: "module_block",
                        snippet: CodeSnippet.Parse("module ${1:name}(\n    input wire ${2:clk}\n);\n    $0\nendmodule"));
                    Add("时序逻辑块", "always @(posedge …)", 15, filter: "always_clock",
                        snippet: CodeSnippet.Parse("always @(posedge ${1:clk}) begin\n    $0\nend"));
                    Add("组合逻辑块", "always @*", 15, filter: "always_comb_block",
                        snippet: CodeSnippet.Parse("always @* begin\n    $0\nend"));
                }
            }
        }
        return suggestions.Take(150).ToArray();

        void Add(string name, string detail, int kind, string? label = null, string? filter = null, CodeSnippet? snippet = null)
        {
            if ((filter ?? name).StartsWith(prefix, StringComparison.Ordinal) && names.Add(name))

            {
                suggestions.Add(new(label ?? name, snippet?.Text ?? name, filter ?? name, detail, "", kind, range, name)
                {
                    Snippet = snippet
                });
            }
        }
    }, token);

    public Task<CodeHover?> ResolveSymbolAsync(string root, string path, string text, int offset, CodeDocumentSnapshot[] documents,
        CancellationToken token) => Task.Run(async () =>
    {
        if (text.Length > 1_048_576)
        {
            return null;
        }
        var document = VerilogDocument.Read(text, token);
        if (document.SymbolAt(offset) is not { } word)
        {
            return null;
        }
        bool macro = word.Text.StartsWith('`');
        string name = macro ? word.Text[1..] : word.Text;
        var binding = word.Offset > 0 && text[word.Offset - 1] == '.' ? document.NamedConnections(word.Offset - 1) : null;
        var local = document.Symbols.FirstOrDefault(s => binding is null && s.Name.Text == name && s.Kind != 5 &&
            (macro ? s.Role == "宏" : s.Role != "宏" && Visible(s, offset)));
        if (local is not null)
        {
            return new CodeHover(local.Role + " " + name, [Location(path, text, local)]);
        }
        var files = await GetFilesAsync(root, path, text, documents, token);
        var locations = new List<CodeLocation>();
        string role = "";
        foreach (var (relative, body, parsed) in ParseFiles(files, token))
        {
            var matches = binding is { } connection
                ? parsed.Symbols.Where(s => s.Kind == 5 && s.Name.Text == connection.Module).SelectMany(s => s.Members)
                    .Where(s => (connection.Parameters ? s.Role == "parameter" : s.Kind == 8) && s.Name.Text == name)
                : parsed.Symbols.Where(s => s.Name.Text == name && (macro ? s.Role == "宏" : s.Kind == 5));
            foreach (var match in matches)
            {
                role = match.Role;
                locations.Add(Location(relative, body, match));
            }
        }
        return locations.Count == 0 ? null : new CodeHover(role + " " + name, locations);
    }, token);

    public static IReadOnlyList<CodeDocumentSymbol> Outline(string text, CancellationToken token)
    {
        if (text.Length > 1_048_576)
        {
            return [];
        }
        return VerilogDocument.Read(text, token).Symbols.Where(s => s.Kind == 5 || s.Scope < 0).Select(Convert).ToArray();
        CodeDocumentSymbol Convert(VerilogDocument.Symbol symbol)
        {
            var name = Range(text, symbol.Name.Offset, symbol.Name.End);
            return new(symbol.Name.Text, symbol.Role, symbol.Kind, symbol.Kind == 5 ? Range(text, symbol.Scope, symbol.ScopeEnd) : name,
                name, symbol.Members.Select(Convert).ToArray());
        }
    }

    private async Task<HdlEditorAnalysis.Snapshot> GetFilesAsync(string? root, string path, string text,
        CodeDocumentSnapshot[] documents, CancellationToken token)
    {
        var drafts = Drafts(documents);
        drafts[Normalize(path)] = text;
        if (root is null)
        {
            return new([], [], [], drafts);
        }
        HdlEditorAnalysis.Snapshot? snapshot = null;
        lock (sync)
        {
            if (indexed is { } cached && cached.Root == root && cached.Path == path && Environment.TickCount64 - cached.Stamp < 1200)

            {
                snapshot = cached.Snapshot;
            }
        }
        if (snapshot is null)
        {
            try
            {
                snapshot = await analysis.CaptureAsync(root, path, text, drafts, token);
                lock (sync)
                {
                    indexed = (root, path, Environment.TickCount64, snapshot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException or System.Text.Json.JsonException)
            {
                return new([], [], [], drafts);
            }
        }
        var files = new Dictionary<string, string>(snapshot.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var (file, body) in drafts)
        {
            files[file] = body;
        }
        return snapshot with
        {
            Files = files
        };
    }

    private static IEnumerable<(string Path, string Text, VerilogDocument Parsed)> ParseFiles(HdlEditorAnalysis.Snapshot snapshot, CancellationToken token)
    {
        foreach (var (path, text) in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            if (text.Length <= 1_048_576)
            {
                yield return (path, text, VerilogDocument.Read(text, token));
            }
        }
    }
    private static bool Visible(VerilogDocument.Symbol symbol, int offset) => offset >= symbol.Scope && offset <= symbol.ScopeEnd;
    private static bool Identifier(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static Dictionary<string, string> Drafts(CodeDocumentSnapshot[] documents) =>
        documents.Where(doc => Supports(doc.Path)).ToDictionary(doc => Normalize(doc.Path), doc => doc.Text, StringComparer.OrdinalIgnoreCase);
    private static CodeRange Range(string text, int start, int end) => new(CodePositions.FromOffset(text, start), CodePositions.FromOffset(text, end));
    private static CodeLocation Location(string path, string text, VerilogDocument.Symbol symbol) =>
        new(path, Range(text, symbol.Name.Offset, symbol.Name.End), path);
}
