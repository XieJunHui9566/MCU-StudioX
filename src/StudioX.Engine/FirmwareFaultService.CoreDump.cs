namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class FirmwareFaultService
{
    public Task<FaultAnalysisReport> DecodeCoreDumpAsync(string project, string dump, string type, CancellationToken token = default)
        => DecodeCoreDumpAsync(project, dump, type, null, token);

    /// <summary>归档 ELF 由用户明确选择；转储和符号快照参与哈希校验，不假造当前工程构建凭据。</summary>
    public Task<FaultAnalysisReport> DecodeCoreDumpAsync(string project, string dump, string type, string? archivedElf, CancellationToken token = default)
        => Task.Run(async () =>
        {
            if (type is not ("b64" or "elf" or "raw")) { throw new ArgumentException("转储格式应为 b64、elf 或 raw。", nameof(type)); }
            var manifest = await ProjectService.ReadAsync(project, token);
            var sdk = manifest.Espressif;
            if (sdk is not { Framework: "esp-idf" }) { throw new StudioXException("FAULT_IDF", "Core Dump 解码需要明确目标的 ESP-IDF 工程。"); }
            var selectedElf = archivedElf;
            ResolvedToolset resolved;
            if (selectedElf is null) { var symbols = await SymbolsAsync(project, token); selectedElf = symbols.Elf; resolved = symbols.Tools; }
            else { resolved = (await tools.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId, token)).ForEspressifTarget(sdk.Target); }
            if (resolved.Manifest.Purpose != "esp-idf" || resolved.Manifest.Version != sdk.SdkVersion) { throw new StudioXException("FAULT_TOOLSET", "工程锁定的 IDF 身份不一致。"); }
            var root = Path.Combine(Path.GetTempPath(), "MCU-StudioX", "faults", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Exception? decodingError = null;
            var diagnostic = "";
            try
            {
                var dumpCopy = Path.Combine(root, "dump." + type);
                var elfCopy = Path.Combine(root, "application.elf");
                var dumpHash = await SnapshotAsync(dump, dumpCopy, 64 * 1024 * 1024, token);
                var elfHash = await SnapshotAsync(selectedElf, elfCopy, 256 * 1024 * 1024, token);
                var bridge = Path.Combine(root, "decode.py");
                await using (var resource = typeof(FirmwareFaultService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-coredump.py")!)
                await using (var target = File.Create(bridge)) { await resource.CopyToAsync(target, token); }
                var environment = ToolsetEnvironment.Create(resolved);
                environment["IDF_PATH"] = resolved.ResourceDirectory("idf");
                environment["PYTHONHOME"] = resolved.ResourceDirectory("python-env");
                environment["PYTHONUTF8"] = "1";
                environment["TEMP"] = root; environment["TMP"] = root;
                environment["PYTHONPYCACHEPREFIX"] = Path.Combine(root, "python-cache");
                var metadata = Path.Combine(root, "evidence.json");
                var result = await runner.RunAsync(new(resolved.Tool("python"), ["-I", "-B", bridge, sdk.Target, type, dumpCopy, elfCopy, resolved.Tool("gdb"), metadata], root,
                    TimeSpan.FromMinutes(2), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
                var output = result.StandardOutput + "\n" + result.StandardError;
                diagnostic = output;
                if (result.ExitCode != 0 || result.TimedOut || result.OutputTruncated || !File.Exists(metadata))
                { throw new StudioXException("FAULT_DUMP", $"本地转储解析失败（exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}）：\n" + output); }
                var detail = await JsonStore.ReadAsync<DecoderEvidence>(metadata, token);
                if (detail.Target != sdk.Target) { throw new StudioXException("FAULT_TARGET", "转储目标与工程不一致。\n" + output); }
                var embedded = detail.EmbeddedElfHash;
                var match = embedded is { Length: >= 8 and <= 64 } && embedded.All(Uri.IsHexDigit) && elfHash.StartsWith(embedded, StringComparison.OrdinalIgnoreCase);
                if (embedded is not null && !match) { throw new StudioXException("FAULT_ELF", "转储中的 ELF 摘要与选择的 ELF 不一致。\n" + output); }
                var evidence = new CoreDumpEvidence(dumpHash, type, detail.Target, sdk.SdkVersion, detail.DecoderVersion, embedded, match,
                    detail.Tasks, detail.CrashedTask, detail.PanicDetails, archivedElf is null ? "当前工程构建记录" : "用户选择的归档 ELF");
                return new FaultAnalysisReport(new(1, manifest.DeviceId, "导入的 ESP Core Dump；离线解析，未连接设备", DateTimeOffset.UtcNow,
                    null, null, null, null, null, null, output),
                    [match ? $"转储的 ELF 摘要已匹配（{embedded!.Length} 个十六进制字符）；不代表本轮连接了实板。" : "此旧格式转储未提供 ELF 摘要，符号对应仍需自行确认。",
                        "只使用工程锁定的工具；保留原始输入，未读取设备、下载 ROM 或执行 ELF 自动加载脚本。"], [], output, elfHash, evidence);
            }
            catch (Exception ex) { decodingError = ex; throw; }
            finally
            {
                try { await DeleteSnapshotAsync(root); }
                catch (Exception cleanup)
                {
                    // 清理失败不能覆盖真正的解码诊断；只清理本次创建的临时目录。
                    if (decodingError is not null) { throw new AggregateException("转储解码及临时目录清理均失败。", decodingError, cleanup); }
                    throw new StudioXException("FAULT_CLEANUP", $"转储解码后未能清理临时目录：{root}\n" + diagnostic, cleanup);
                }
            }
        }, token);

    private sealed record DecoderEvidence(string Target, string DecoderVersion, string? EmbeddedElfHash,
        IReadOnlyList<CoreDumpTask> Tasks, string? CrashedTask, string? PanicDetails);
    private static async Task DeleteSnapshotAsync(string root)
    {
        // Windows 工具退出时仍可能短暂持有映像；限时重试，不将文件占用误报为解码失败。
        for (var attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(root)) { Directory.Delete(root, true); } return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(100 * (attempt + 1)); }
        }
    }
    private static async Task<string> SnapshotAsync(string source, string target, int maximum, CancellationToken token)
    {
        await using var input = new FileStream(Path.GetFullPath(source), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) { throw new StudioXException("FAULT_SIZE", "转储或 ELF 为空或超过大小限制。"); }
        await using (var output = File.Create(target)) { await input.CopyToAsync(output, token); }
        input.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
}
