namespace StudioX.Application.Plugins;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudioX.Application.BuildConfiguration;
using StudioX.Engine;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>图形代码生成的应用边界；来源、所有权和文本接入均在写入前验证。</summary>
public sealed partial class McuCodeGenerationService(PluginManagerService plugins, ProjectFileService files,
    ProjectClockService clocks, string dataDirectory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Pending = ".studiox/mcu-generation.pending.json";

    /// <summary>文件同步完成后通知插件读取提交结果；哈希不匹配时保留插件草稿。</summary>
    public async Task RefreshAsync(PluginWorkspaceSession workspace, string pluginId, string commandId, CancellationToken token = default)
    {
        var source = await files.ReadAsync(workspace.Project!, "device.mcuconfig", token).ConfigureAwait(false);
        var saved = JsonNode.Parse(source.Text)!;
        var hash = saved["generation"]?["inputSha256"]?.GetValue<string>() ?? throw Invalid("没有已提交的生成凭据。");
        await workspace.InvokeAsync(pluginId, "command", commandId, JsonSerializer.SerializeToElement(new
        {
            action = "generationApplied",
            inputSha256 = hash
        }), token).ConfigureAwait(false);
    }

    public async Task<int> GenerateAsync(PluginWorkspaceSession workspace, string pluginId, string commandId,
        Func<Task<bool>> hasDirtyBuffers, CancellationToken token = default)
    {
        var root = workspace.Project ?? throw Invalid("生成代码需要工程。");
        var gate = Gates.GetOrAdd(Path.GetFullPath(root), _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(root, token).ConfigureAwait(false);
            await ReadyAsync();
            var entry = await ProviderAsync();
            var result = await workspace.InvokeAsync(pluginId, "command", commandId,
                JsonSerializer.SerializeToElement(new
                {
                    action = "generate"
                }), token).ConfigureAwait(false);
            var plan = result.Deserialize<PluginMcuGenerationPlan>(JsonStore.Options) ?? throw Invalid("生成计划为空。");
            if (plan.FormatVersion != 1 || plan.Files is not { Length: > 0 and <= 32 } ||
                plan.Files.Any(file => file is null || file.Content is null || file.Content.Length > 32000 || file.Content.Contains('\0')) ||
                plan.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Length ||
                plan.Files.Sum(file => file.Content.Length) > 128 * 1024)
            {
                throw Invalid("生成计划版本、文件集合或容量无效。");
            }
            var current = await files.ReadAsync(root, "device.mcuconfig", token).ConfigureAwait(false);
            if (current.IsReadOnly || !current.DiskHash.Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("配置文件在读取后已变化或为只读；请保留草稿并重新读取。");
            }
            var old = JsonNode.Parse(current.Text)!.AsObject();
            var candidate = JsonNode.Parse(plan.Configuration.GetRawText())!.AsObject();
            foreach (var key in new[] { "format", "formatVersion", "documentKind", "origin", "target", "profile", "definitions", "generation" })
            {
                if (!JsonNode.DeepEquals(old[key], candidate[key]))
                {
                    throw Invalid("生成计划改变了受保护字段：" + key);
                }
            }
            var validation = await clocks.ValidateCandidateAsync(root, Utf8.GetBytes(candidate.ToJsonString()), token).ConfigureAwait(false);
            if (!validation.Managed || !validation.Ready || candidate["profile"]?["pluginId"]?.GetValue<string>() != pluginId)
            {
                throw Invalid(validation.Message);
            }
            var binding = candidate["definitions"]!["profileData"]!["codeGeneration"]!;
            if (binding["contractVersion"]?.GetValue<int>() != 1)
            {
                throw Invalid("模板未声明代码生成接入契约。");
            }
            var header = binding["entryHeader"]!.GetValue<string>();
            var function = binding["entryFunction"]!.GetValue<string>();
            var mainPath = binding["mainPath"]!.GetValue<string>();
            if (!Regex.IsMatch(function, "^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant) ||
                !SafeOutput("include/" + header) || !plan.Files.Any(file => file.Path == "include/" + header) ||
                mainPath != "src/main.c")
            {
                throw Invalid("模板入口声明无效。");
            }
            var previous = Outputs(old["generation"]);
            var desired = plan.Files.ToDictionary(file => file.Path, file => (byte[]?)Utf8.GetBytes(file.Content), StringComparer.Ordinal);
            foreach (var path in desired.Keys.Concat(previous.Keys))
            {
                if (!SafeOutput(path))
                {
                    throw Invalid("只能生成专用目录内的 C 源码和头文件：" + path);
                }
            }
            foreach (var path in previous.Keys.Except(desired.Keys))
            {
                desired.Add(path, null);
            }
            var changes = new List<GenerationFileChange>();
            foreach (var (path, after) in desired.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var before = await BytesAsync(root, path, token).ConfigureAwait(false);
                if (previous.TryGetValue(path, out var owned))
                {
                    if (Hash(before) != owned)
                    {
                        throw Invalid("生成文件被修改或删除，未覆盖：" + path);
                    }
                }
                else if (before is not null)
                {
                    throw Invalid("同名文件不属于生成器，未覆盖：" + path);
                }
                changes.Add(new(path, before, after));
            }
            var main = await files.ReadAsync(root, mainPath, token).ConfigureAwait(false);
            if (main.IsReadOnly)
            {
                throw Invalid("main.c 为只读。");
            }
            var include = "#include \"" + header + "\"";
            var updatedMain = ReplaceBlock(main.Text, "/* StudioX MCU includes begin */", "/* StudioX MCU includes end */", include, previous.Count > 0 ? include : "");
            updatedMain = ReplaceInitialization(updatedMain, function, previous.Count > 0);
            changes.Add(new(mainPath, await SnapshotAsync(main), Encode(main, updatedMain)));
            var cmake = await files.ReadAsync(root, "CMakeLists.txt", token).ConfigureAwait(false);
            if (cmake.IsReadOnly)
            {
                throw Invalid("CMakeLists.txt 为只读。");
            }
            var start = "# StudioX MCU sources begin";
            var end = "# StudioX MCU sources end";
            string Sources(IEnumerable<string> paths) => "target_sources(firmware PRIVATE\n" + string.Join('\n', paths.Where(path => path.EndsWith(".c", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(path => "    \"" + path + "\"")) + "\n)";
            var cmakeText = cmake.Text;
            var sourceTargets = new SourceListEditor(cmakeText, false).Describe(path => path);
            if (!sourceTargets.Any(target => target.Name == "firmware" && target.Sources.Contains("src/main.c", StringComparer.Ordinal)))
            {
                throw Invalid("构建列表没有明确的 firmware / src/main.c 接入点。");
            }
            if (previous.Count == 0 && !cmakeText.Contains(start, StringComparison.Ordinal))
            {
                cmakeText += "\n\n" + start + "\n" + end + "\n";
            }
            cmakeText = ReplaceBlock(cmakeText, start, end, Sources(plan.Files.Select(file => file.Path)), previous.Count > 0 ? Sources(previous.Keys) : "");
            _ = new SourceListEditor(cmakeText, false).Describe(path => path);
            changes.Add(new("CMakeLists.txt", await SnapshotAsync(cmake), Encode(cmake, cmakeText)));
            var inputHash = ProjectClockService.InputHash(candidate);
            candidate["generation"] = JsonSerializer.SerializeToNode(new
            {
                generator = new
                {
                    pluginId,
                    version = entry.Version,
                    contentSha256 = entry.ContentFingerprint,
                    providerId = candidate["profile"]!["providerId"]!.GetValue<string>()
                },
                inputSha256 = inputHash,
                outputs = plan.Files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => new { path = file.Path, sha256 = Hash(Utf8.GetBytes(file.Content)) }).ToArray()
            });
            changes.Add(new("device.mcuconfig", await SnapshotAsync(current), Utf8.GetBytes(candidate.ToJsonString(JsonStore.Options) + "\n")));
            await ReadyAsync();
            var latest = await ProviderAsync();
            if (latest.ContentFingerprint != entry.ContentFingerprint)
            {
                throw Invalid("生成期间插件内容已变化。");
            }
            foreach (var change in changes)
            {
                await CheckAsync(root, change, token).ConfigureAwait(false);
            }
            await CommitAsync(root, changes.Where(change => Hash(change.Before) != Hash(change.After)).ToArray(), hasDirtyBuffers, token).ConfigureAwait(false);
            return plan.Files.Length;

            async Task<byte[]> SnapshotAsync(SourceDocument source)
            {
                var snapshot = await BytesAsync(root, source.RelativePath, token).ConfigureAwait(false);
                if (!string.Equals(Hash(snapshot), source.DiskHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid("读取期间文件已变化：" + source.RelativePath);
                }
                return snapshot!;
            }

            async Task ReadyAsync()
            {
                token.ThrowIfCancellationRequested();
                if (await hasDirtyBuffers().ConfigureAwait(false))
                {
                    throw Invalid("请先保存工程文本编辑器中的修改，再生成代码。");
                }
                if (!workspace.IsPluginRunning(pluginId))
                {
                    throw Invalid("配置插件已停止。");
                }
            }
            async Task<PluginCatalogEntry> ProviderAsync()
            {
                var provider = (await plugins.ListAsync(token).ConfigureAwait(false)).Single(item => item.Id == pluginId);
                if (!provider.Enabled || !provider.Capabilities.Contains("mcuCodeGenerationV1", StringComparer.Ordinal) ||
                    !workspace.MatchesCatalogEntry(provider) || !await PluginProjectMatcher.MatchesAsync(provider.Manifest!, root, token).ConfigureAwait(false))
                {
                    throw Invalid("插件内容、授权或工程组合已变化。");
                }
                return provider;
            }
        }
        finally { gate.Release(); }
    }

    private static Dictionary<string, string> Outputs(JsonNode? generation)
    {
        if (generation is null)
        {
            return new(StringComparer.Ordinal);
        }
        var outputs = generation["outputs"]!.AsArray();
        if (outputs.Count is < 1 or > 32)
        {
            throw Invalid("生成凭据输出数量无效。");
        }
        return outputs.ToDictionary(item => item!["path"]!.GetValue<string>(), item => item!["sha256"]!.GetValue<string>(), StringComparer.Ordinal);
    }
    private static bool SafeOutput(string path) => Regex.IsMatch(path, "^(src|include)/generated/mcuconfig/[A-Za-z][A-Za-z0-9_]{0,63}\\.[ch]\\z", RegexOptions.CultureInvariant);
    private static byte[] Encode(SourceDocument source, string text) => [.. source.Encoding.GetPreamble(), .. source.Encoding.GetBytes(text)];
    private static string ReplaceBlock(string text, string begin, string end, string replacement, string expected)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (first, last, content) = ReadBlock(text, begin, end);
        if (content != expected)
        {
            throw Invalid("自动生成的接入内容已被修改；未改动用户代码：" + begin);
        }
        return text[..(first + begin.Length)] + newline + (replacement.Length > 0 ? replacement.Replace("\n", newline, StringComparison.Ordinal) + newline : "") + text[last..];
    }
    private static (int First, int Last, string Content) ReadBlock(string text, string begin, string end)
    {
        var first = text.IndexOf(begin, StringComparison.Ordinal);
        var last = text.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last < first || text.IndexOf(begin, first + begin.Length, StringComparison.Ordinal) >= 0 || text.IndexOf(end, last + end.Length, StringComparison.Ordinal) >= 0)
        {
            throw Invalid("模板接入标记缺失或重复；未改动用户代码：" + begin);
        }
        return (first, last, text[(first + begin.Length)..last].Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n'));
    }
    private static string ReplaceInitialization(string text, string function, bool generated)
    {
        const string begin = "    /* StudioX MCU init begin */";
        const string end = "    /* StudioX MCU init end */";
        var (_, _, content) = ReadBlock(text, begin, end);
        var call = "    " + function + "();";
        // 入口来自不可变模板；已有调用完整时无需重写整块，后续手写语句连同原始换行一起保留。
        if (generated && (content == call || content.StartsWith(call + "\n", StringComparison.Ordinal)))
        {
            return text;
        }
        var expected = generated ? "    if (!" + function + "()) { for (;;) { } }" : "";
        if (content != expected)
        {
            throw Invalid(generated
                ? "src/main.c 的自动初始化调用已被修改。请保留 init begin 后的 " + function + "();，用户代码可写在该调用之后。本次未改动文件。"
                : "首次生成要求 src/main.c 的 init begin/end 标记之间为空。请将用户代码移至 init end 之后再生成。本次未改动文件。");
        }
        // 只迁移完整的旧版失败循环；不根据其它用户函数或注释中的同名代码推断接入方式。
        return ReplaceBlock(text, begin, end, call, expected);
    }
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static StudioXException Invalid(string message) => new("MCU_CODE_GENERATION", message);
}
