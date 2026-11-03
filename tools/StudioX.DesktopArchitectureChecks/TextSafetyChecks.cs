using System.Text;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

internal static class TextSafetyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task Reject(Func<Task> operation, string code, string label)
        {
            try { await operation(); }
            catch (StudioXException error) when (error.Code == code) { check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        var root = Path.Combine(Path.GetTempPath(), "studiox-text-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new ProjectFileService();
            var path = Path.Combine(root, "main.unknown");
            const string original = "safe text marker\n";
            await File.WriteAllTextAsync(path, original);
            var source = await files.ReadAsync(root, "main.unknown");
            var longLine = new string('x', EditorTextSafety.MaximumLineLength + 1);
            var complex = "a" + new string('\u0301', EditorTextSafety.MaximumTextElementLength);
            await Reject(() => files.SaveAsync(root, source, longLine), "EDITOR_LINE_LENGTH", "保存前拒绝超长行");
            await Reject(() => files.SaveAsync(root, source, complex), "EDITOR_TEXT_COMPLEXITY", "保存前拒绝超长 Unicode 簇");
            await Reject(() => files.SaveAsync(root, source, "binary\0data"), "EDITOR_BINARY", "保存前拒绝 NUL 正文");
            check(await File.ReadAllTextAsync(path) == original && !Directory.EnumerateFiles(root, "*.tmp-*").Any(), "拒绝保存保留原文件且不产生临时文件");
            var valid = new WorkspaceFileChange(source, original, original + "// edit\n", [new(original.Length, 0, "// edit\n")], false);
            var unsafeChange = valid with { After = complex };
            await Reject(() => new WorkspaceEditService(files).ValidateAsync(root, [unsafeChange], []), "EDITOR_TEXT_COMPLEXITY", "AI 与批量修改在写入缓冲区前拒绝危险正文");
            check(await File.ReadAllTextAsync(path) == original, "被拒绝的修改计划不改变磁盘");
            await File.WriteAllTextAsync(Path.Combine(root, "unsafe.unknown"), complex);
            var search = await new WorkspaceEditService(files).SearchAsync(root, new("marker"), null, "", "", false, []);
            check(search.Files.Count == 1 && search.Notices.Any(notice => notice.StartsWith("unsafe.unknown", StringComparison.Ordinal)), "工程搜索跳过危险文件并保留提示和其他结果");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                try { EditorTextSafety.Validate("valid", stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
                catch (OperationCanceledException) { check(true, "正文检查响应取消"); }
            }
            var snapshot = new EditorWorkspaceSnapshot(1, root, "main.unknown",
                [new("main.unknown", complex, original, source.DiskHash, 65001, false, false, 0, 0, 0, 0, 0)], DateTimeOffset.UtcNow);
            var data = Path.Combine(root, "user-data");
            using (var oldStore = new EditorSessionStore(data))
            {
                await Reject(() => oldStore.SaveAsync(snapshot), "EDITOR_TEXT_COMPLEXITY", "草稿备份在持久化前校验排版复杂度");
                var good = snapshot with { Documents = [snapshot.Documents[0] with { Draft = original + "// draft\n" }] };
                await oldStore.SaveAsync(good);
            }
            var state = Directory.EnumerateFiles(data, "state.json", SearchOption.AllDirectories).Single();
            await JsonStore.WriteAsync(state, snapshot);
            var stateBytes = await File.ReadAllBytesAsync(state);
            using (var newStore = new EditorSessionStore(data))
            {
                await Reject(() => newStore.ClaimLatestAsync(), "EDITOR_TEXT_COMPLEXITY", "旧恢复记录不会绕过排版保护导致重启崩溃");
            }
            check((await File.ReadAllBytesAsync(state)).SequenceEqual(stateBytes), "被拒绝的恢复记录逐字节保留以便手动救援");
            EditorTextSafety.Validate("中文 a\u0301 👩‍👩‍👧‍👦\r\n" + new string('x', EditorTextSafety.MaximumLineLength));
            check(true, "普通组合字符、emoji 和单行长度边界仍可编辑");
        }
        finally { Directory.Delete(root, true); }
    }
}
