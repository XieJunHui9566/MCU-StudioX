namespace StudioX.Application.CodeIntelligence;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class CodeIntelligenceService(string runtimeDirectory, string dataDirectory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentQueue<string> log = new();
    private LanguageServerConnection? connection;
    private string projectRoot = "";
    private string[] flags = [];
    private string? compilerHeaders;
    private readonly Dictionary<string, string> synchronizedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> documentsNeedingReparse = new(StringComparer.OrdinalIgnoreCase);
    private int version;
    private bool supportsAst;
    public bool IsReady => connection?.IsRunning == true;
    public IEnumerable<string> DrainLog() { while (log.TryDequeue(out var line)) yield return line; }
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx";

    public Task StartAsync(string directory, CancellationToken token = default)
        => Task.Run(() => StartInBackgroundAsync(directory, token), token);
    private async Task StartInBackgroundAsync(string directory, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (connection is { } previous) { connection = null; await previous.DisposeAsync().ConfigureAwait(false); }
            synchronizedDocuments.Clear(); documentsNeedingReparse.Clear(); version = 0;
            projectRoot = Path.GetFullPath(directory);
            var project = await ProjectService.ReadAsync(projectRoot, token).ConfigureAwait(false);
            importedCommands.Clear();
            var executable = Path.Combine(runtimeDirectory, "languages", "clangd", "bin", "clangd.exe");
            if (!File.Exists(executable)) throw new StudioXException("LANGUAGE_MISSING", "此安装缺少内置 clangd 组件，代码提示不可用。");
            if (project.Kind == ProjectKind.CubeMx) flags = ["--target=arm-none-eabi", "-ffreestanding", "-ferror-limit=0"];
            else
            {
                var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(projectRoot, "device/manifest.json"), token).ConfigureAwait(false);
                var device = TemplateResolver.Resolve(pack.Devices.Single(d => d.Id == project.DeviceId), project.TemplateId);
                flags = CreateFlags(device);
            }
            compilerHeaders = null;
            var compilerTriple = project.CompilerId switch
            {
                "arm-gnu-15.2.rel1" => "arm-none-eabi",
                "wch-gcc-12.2.0-v1.4" => "riscv-wch-elf",
                _ => null
            };
            if (compilerTriple is not null)
            {
                var headers = PathBoundary.Resolve(runtimeDirectory, $"toolsets/{project.ToolsetId}/{project.ToolsetVersion}/gcc/{compilerTriple}/include");
                if (Directory.Exists(headers)) { compilerHeaders = headers; flags = [..flags, "-isystem", headers.Replace('\\', '/')]; }
            }
            else if (project is { ToolsetId: "stc.sdcc", CompilerId: "sdcc-4.5.0-15242" })
            {
                var headers = PathBoundary.Resolve(runtimeDirectory, $"toolsets/{project.ToolsetId}/{project.ToolsetVersion}/sdcc/include");
                if (Directory.Exists(headers)) { compilerHeaders = headers; flags = [..flags, "-isystem", headers.Replace('\\', '/')]; }
            }
            var cache = Path.Combine(dataDirectory, "language-cache", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectRoot)))[..16]);
            Directory.CreateDirectory(cache);
            var analysisCommands = project.Kind == ProjectKind.CubeMx
                ? await CreateImportedDatabaseAsync(cache, token).ConfigureAwait(false)
                : await CreateNavigationDatabaseAsync(cache, token).ConfigureAwait(false);
            var server = new LanguageServerConnection(executable, projectRoot, cache, line =>
            {
                log.Enqueue(line); while (log.Count > 200) log.TryDequeue(out _);
            });
            try
            {
                var initialization = await server.RequestAsync("initialize", new
                {
                    processId = Environment.ProcessId, rootUri = new Uri(projectRoot + Path.DirectorySeparatorChar).AbsoluteUri,
                    clientInfo = new { name = "MCU StudioX", version = "0.1.0" },
                    capabilities = new
                    {
                        general = new { positionEncodings = new[] { "utf-16" } },
                        textDocument = new
                        {
                            hover = new { contentFormat = new[] { "plaintext" } },
                            definition = new { linkSupport = true }, declaration = new { linkSupport = true },
                            documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                            completion = new { completionItem = new { snippetSupport = false, documentationFormat = new[] { "plaintext" }, insertReplaceSupport = false } },
                            signatureHelp = new { signatureInformation = new { documentationFormat = new[] { "plaintext" }, parameterInformation = new { labelOffsetSupport = true } } }
                        }
                    },
                    initializationOptions = new { compilationDatabasePath = cache, fallbackFlags = flags }
                }, token).ConfigureAwait(false);
                supportsAst = initialization.TryGetProperty("capabilities", out var serverCapabilities) &&
                    serverCapabilities.TryGetProperty("astProvider", out var astProvider) && astProvider.ValueKind == JsonValueKind.True;
                await server.NotifyAsync("initialized", new { }, token).ConfigureAwait(false);
                await server.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { compilationDatabaseChanges = analysisCommands } }, token).ConfigureAwait(false);
                connection = server;
            }
            catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { gate.Release(); }
    }

    private string[] CreateFlags(DeviceDefinition device)
    {
        var target = device.Architecture.ToLowerInvariant() switch
        {
            "riscv" => device.CpuFlags.Any(f => f.StartsWith("-march=rv64", StringComparison.Ordinal)) ? "riscv64-unknown-elf" : "riscv32-unknown-elf",
            "arm" or "cortex-m" => "arm-none-eabi",
            // clangd 没有 MCS-51 目标；在此只提供通用 C 编辑索引，构建仍由 SDCC 执行。
            "mcs51" => null,
            _ => throw new StudioXException("LANGUAGE_TARGET", "当前代码提示服务尚未支持此器件架构：" + device.Architecture)
        };
        // 只传递影响 C/C++ 解析的参数；不执行编译器，也不把链接/下载参数交给语言服务。
        var result = new List<string> { "-ffreestanding", "-ferror-limit=0" };
        if (target is not null) result.Insert(0, "--target=" + target);
        if (device.Architecture.Equals("mcs51", StringComparison.OrdinalIgnoreCase))
        {
            // 仅让寄存器声明和地址空间标记在 clangd 中保持可解析；宽度、指针及中断语义以 SDCC 为准。
            result.AddRange([
                "-D__sfr=volatile unsigned char", "-D__sfr16=volatile unsigned short", "-D__sbit=volatile unsigned char",
                "-D__bit=unsigned char", "-D__at(x)=", "-D__data=", "-D__idata=", "-D__pdata=", "-D__xdata=", "-D__code=",
                "-D__interrupt(x)=", "-D__using(x)=", "-D__reentrant=", "-D__critical=", "-D__naked=", "-D__banked=", "-D__nonbanked="
            ]);
        }
        var cpuFlags = device.CpuFlags;
        // clangd 不识别沁恒 XW 扩展；仅语言分析使用标准 ISA，实际编译仍使用包内原始参数。
        if (device.CompilerId == "wch-gcc-12.2.0-v1.4")
            cpuFlags = cpuFlags.Select(f => f == "-march=rv32imac_xw" ? "-march=rv32imac_zicsr" : f).ToArray();
        result.AddRange(cpuFlags.Where(f => f.StartsWith("-march=", StringComparison.Ordinal) || f.StartsWith("-mabi=", StringComparison.Ordinal) ||
            f.StartsWith("-mcpu=", StringComparison.Ordinal) || f.StartsWith("-mfpu=", StringComparison.Ordinal) || f.StartsWith("-mfloat-abi=", StringComparison.Ordinal) || f == "-mthumb"));
        result.AddRange(device.IncludeDirectories.Select(path => "-I" + PathBoundary.Resolve(projectRoot, "device/" + path).Replace('\\', '/')));
        result.Add("-I" + Path.Combine(projectRoot, "src").Replace('\\', '/'));
        result.Add("-I" + Path.Combine(projectRoot, "include").Replace('\\', '/'));
        result.AddRange(device.Defines.Select(define => "-D" + define));
        var standardHeaders = PathBoundary.Resolve(runtimeDirectory, "languages/sysroots/" + device.CompilerId + "/include");
        if (Directory.Exists(standardHeaders)) { result.Add("-isystem"); result.Add(standardHeaders.Replace('\\', '/')); }
        return result.ToArray();
    }

    private async Task<string> SynchronizeAsync(string relativePath, string text, CancellationToken token, bool forceReparse = false)
    {
        var server = connection ?? throw new StudioXException("LANGUAGE_NOT_READY", "代码提示服务尚未就绪。");
        var path = ResolveDocumentPath(relativePath);
        var uri = new Uri(path).AbsoluteUri;
        if (!synchronizedDocuments.TryGetValue(uri, out var previousText) || previousText != text)
            documentsNeedingReparse.UnionWith(synchronizedDocuments.Keys.Where(other => !other.Equals(uri, StringComparison.OrdinalIgnoreCase)));
        forceReparse |= documentsNeedingReparse.Contains(uri);
        if (forceReparse && synchronizedDocuments.ContainsKey(uri))
        {
            // didChange 的异步 preamble 更新可能短暂复用旧 AST；重开引用方后等待首轮解析。
            await server.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } }, token).ConfigureAwait(false);
            synchronizedDocuments.Remove(uri);
        }
        if (!synchronizedDocuments.TryGetValue(uri, out var synchronizedText))
        {
            var cpp = IsCpp(path);
            var command = AnalysisCommand(path);
            var changes = new Dictionary<string, object> { [path] = new { workingDirectory = AnalysisDirectory(path), compilationCommand = command } };
            await server.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { compilationDatabaseChanges = changes } }, token).ConfigureAwait(false);
            await server.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = cpp ? "cpp" : "c", version = ++version, text } }, token).ConfigureAwait(false);
            synchronizedDocuments[uri] = text;
            // 首次打开等待 AST 与头文件符号就绪，避免第一轮只返回无类型的词语候选。
            documentsNeedingReparse.Add(uri);
            await server.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } }, token).ConfigureAwait(false);
        }
        else if (synchronizedText != text)
        {
            await server.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version = ++version }, contentChanges = new[] { new { text } } }, token).ConfigureAwait(false);
            synchronizedDocuments[uri] = text;
            documentsNeedingReparse.Add(uri);
            await server.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } }, token).ConfigureAwait(false);
        }
        documentsNeedingReparse.Remove(uri);
        return uri;
    }

    public async Task<IReadOnlyList<CodeSuggestion>> CompleteAsync(string relativePath, string text, int offset, CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(relativePath)) return [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(relativePath, text, documents, token).ConfigureAwait(false);
            var position = CodePositions.FromOffset(text, offset);
            var parameters = new { textDocument = new { uri }, position = new { line = position.Line, character = position.Character }, context = new { triggerKind = 1 } };
            var response = await connection!.RequestAsync("textDocument/completion", parameters, token).ConfigureAwait(false);
            JsonElement Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var values) ? values : default;
            var items = Items(response);
            // 首轮 AST 完成后，clangd 的头文件索引可能仍在提交；空结果只补查一次，输入变化可随时取消。
            if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                items = Items(await connection.RequestAsync("textDocument/completion", parameters, token).ConfigureAwait(false));
            }
            if (items.ValueKind != JsonValueKind.Array) return [];
            var result = new List<CodeSuggestion>();
            foreach (var item in items.EnumerateArray())
            {
                // 当前只接受当前文件内的普通文本替换，不隐式执行命令或插入其他文件的内容。
                if (item.TryGetProperty("insertTextFormat", out var format) && format.GetInt32() == 2) continue;
                if (item.TryGetProperty("additionalTextEdits", out var additional) && additional.GetArrayLength() > 0) continue;
                var label = String(item, "label"); var insertion = String(item, "insertText", label); CodeRange? range = null;
                if (item.TryGetProperty("textEdit", out var edit))
                {
                    insertion = String(edit, "newText", insertion);
                    if (edit.TryGetProperty("range", out var span)) range = JsonSerializer.Deserialize<CodeRange>(span, JsonStore.Options);
                }
                result.Add(new(label, insertion, String(item, "filterText", insertion), String(item, "detail"), Documentation(item),
                    item.TryGetProperty("kind", out var kind) ? kind.GetInt32() : 1, range, String(item, "sortText", label)));
            }
            return result.OrderBy(item => item.SortText, StringComparer.Ordinal).ToArray();
        }
        finally { gate.Release(); }
    }
    public async Task<CodeSignature?> SignatureAsync(string relativePath, string text, int offset, CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(relativePath)) return null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(relativePath, text, documents, token).ConfigureAwait(false);
            var position = CodePositions.FromOffset(text, offset);
            var response = await connection!.RequestAsync("textDocument/signatureHelp", new { textDocument = new { uri }, position = new { line = position.Line, character = position.Character } }, token).ConfigureAwait(false);
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("signatures", out var signatures) || signatures.GetArrayLength() == 0) return null;
            var index = response.TryGetProperty("activeSignature", out var active) ? active.GetInt32() : 0;
            var signature = signatures[Math.Clamp(index, 0, signatures.GetArrayLength() - 1)];
            var label = String(signature, "label"); var parameters = new List<string>();
            if (signature.TryGetProperty("parameters", out var list))
                foreach (var parameter in list.EnumerateArray())
                {
                    var value = parameter.GetProperty("label");
                    parameters.Add(value.ValueKind == JsonValueKind.String ? value.GetString()! : label[value[0].GetInt32()..value[1].GetInt32()]);
                }
            var activeParameter = signature.TryGetProperty("activeParameter", out var localParameter) ? localParameter.GetInt32() :
                response.TryGetProperty("activeParameter", out var globalParameter) ? globalParameter.GetInt32() : 0;
            return new(label, Documentation(signature), parameters, activeParameter);
        }
        finally { gate.Release(); }
    }
    private static string String(JsonElement element, string name, string fallback = "") => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private static string Documentation(JsonElement element) => element.TryGetProperty("documentation", out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString()! : String(value, "value") : "";
    /// <summary>结束当前工程的语言会话；服务仍可用于随后打开的工程。</summary>
    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = connection; connection = null;
            projectRoot = ""; flags = []; compilerHeaders = null; version = 0;
            importedCommands.Clear();
            synchronizedDocuments.Clear(); documentsNeedingReparse.Clear();
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
