namespace StudioX.KeilImporter;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>先写专用临时目录并验证，再发布为全新工程；不会修改绑定工程或原 Keil 工程。</summary>
public static class ProjectCreator
{
    public static async Task<CreationResult> CreateAsync(ImportPreview preview, string confirmedPreviewId, CancellationToken token)
    {
        if (!preview.CanCreate || preview.PreviewId != confirmedPreviewId)
        {
            throw new InvalidOperationException("预览未通过或尚未确认，请先修正并重新预览。");
        }
        var refreshed = await ImportPlanner.PlanAsync(preview.Request, token);
        if (!refreshed.CanCreate || refreshed.PreviewId != preview.PreviewId)
        {
            throw new InvalidOperationException("输入文件、器件包或 CLI 在预览后改变，请重新预览并确认。");
        }
        var parent = ImportPaths.Absolute(preview.Request.ParentDirectory);
        var staging = Path.Combine(parent, ".studiox-keil-import-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(staging, "project");
        Directory.CreateDirectory(staging);
        try
        {
            var request = preview.Request;
            Directory.CreateDirectory(project);
            var fileEvidence = new List<object>();
            var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in preview.Files)
            {
                token.ThrowIfCancellationRequested();
                var destination = ImportPaths.PackPath(project, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyVerifiedAsync(file, destination, token);
                var edits = preview.Edits.Where(edit => edit.RelativePath == file.RelativePath).ToArray();
                if (edits.Length > 0)
                {
                    await SourceCompatibility.ApplyAsync(destination, edits, token);
                }
                var generatedHash = await ImportPaths.HashAsync(destination, token);
                sourceHashes.Add(file.RelativePath, generatedHash);
                fileEvidence.Add(new
                {
                    path = file.RelativePath,
                    originalBytes = file.Bytes,
                    originalSha256 = file.Sha256,
                    generatedSha256 = generatedHash,
                    compatibilityEdits = edits.Length
                });
            }
            await PortBuildSupport.WriteAsync(preview, project, CopyVerifiedAsync, token);
            await File.WriteAllTextAsync(Path.Combine(project, "CMakeLists.txt"), preview.CMake, new UTF8Encoding(false), token);
            // 使用工程格式已有的入口字段，让首次打开显示真正迁移的 main。
            var mainFiles = preview.ApplicationSources.Where(source => Path.GetFileNameWithoutExtension(source).Equals("main", StringComparison.OrdinalIgnoreCase)).ToArray();
            var manifestNode = JsonNode.Parse((await DeviceCatalog.ReadJsonAsync(Path.Combine(project, ".studiox", "project.json"), token)).GetRawText())!;
            manifestNode["entryFile"] = mainFiles.Length == 1 ? mainFiles[0] : "CMakeLists.txt";
            await File.WriteAllTextAsync(Path.Combine(project, ".studiox", "project.json"), manifestNode.ToJsonString(DeviceCatalog.Json), token);
            var report = new
            {
                formatVersion = 1,
                plugin = "studiox.keil-importer",
                version = "0.1.5",
                mode = "port-existing-project-preserve-layout",
                preview.PreviewId,
                sourceProject = ImportPaths.Relative(request.SourceRoot, request.ProjectFile),
                target = preview.Target.Name,
                originalDevice = preview.Target.Device,
                selectedDevice = request.Device.Id,
                request.Device.PackId,
                request.Device.PackVersion,
                request.Device.ContentHash,
                preview.ApplicationSources,
                preview.IncludeDirectories,
                preview.Target.Defines,
                preview.AdditionalDefines,
                preview.Edits,
                preview.Target.Excluded,
                preview.PackSources,
                preview.Issues,
                sourceFiles = fileEvidence,
                verification = "original-layout-and-input-hashes-verified; see-keil-build.json-for-compilation; hardware-not-tested"
            };
            await File.WriteAllTextAsync(Path.Combine(project, ".studiox", "keil-import.json"), JsonSerializer.Serialize(report, DeviceCatalog.Json), token);
            await File.WriteAllTextAsync(Path.Combine(project, "KEIL-MIGRATION.md"),
                "# Keil5 工程移植记录\n\n原工程未被修改。移植副本保留原源码、资源和目录结构，编译列表仅使用所选 Target，不生成或替换模板 main。\n\n" +
                "仅补充 .studiox/ 元数据、CMakeLists.txt 和 device/ 内的 GCC 构建支持。原 Keil 工程文件和启动汇编保留；GNU 启动文件和链接脚本来自明确选定的格式 1 器件包，差异须人工核对。\n\n" +
                "详细文件 SHA-256、排除项和警告见 .studiox/keil-import.json。工具路径与系统 PATH 未写入工程。\n\n" +
                string.Join("\n", preview.Issues.Select(issue => "- " + issue.Code + ": " + issue.Message)) +
                "\n\n自动编译结果见 .studiox/keil-build.json，完整输出见 .studiox/keil-build.log。请在 StudioX 中打开本目录，核对时钟、堆栈、stdio 重定向、中断和链接布局。编译成功不代表实板验收。\n", token);
            // 输入和工具在最后发布前再核验；仅最终目录发布成功才向用户报告完成。
            foreach (var file in preview.Files)
            {
                if (await ImportPaths.HashAsync(file.Source, token) != file.Sha256)
                {
                    throw new InvalidOperationException("复制期间原工程文件变化，未发布新工程：" + file.RelativePath);
                }
            }
            if (await ImportPaths.HashAsync(request.CliPath, token) != preview.CliHash)
            {
                throw new InvalidOperationException("操作期间 CLI 变化，未发布新工程。");
            }
            _ = await StudioXCli.RunAsync(request.CliPath, ["project-info", project], token);
            ImportPaths.RejectLinks(parent);
            ImportPaths.RejectLinks(preview.Destination);
            token.ThrowIfCancellationRequested();
            // 不覆盖并发创建的目录；失败则清理自有临时工程，原工程始终只读。
            Directory.Move(project, preview.Destination);
            (bool Verified, string? Error) compilation;
            try
            {
                compilation = await PortCompilation.VerifyAsync(request.CliPath, preview.Destination, token, sourceHashes);
            }
            catch (OperationCanceledException error)
            {
                throw new ProjectCreationCanceledException(new(preview.Destination, request.Device.Id, preview.Files.Length,
                    preview.PreviewId, false, PortCompilation.LogPath, "自动编译已取消；移植副本已保留，可打开工程继续编译。"), error);
            }
            return new(preview.Destination, request.Device.Id, preview.Files.Length, preview.PreviewId,
                compilation.Verified, PortCompilation.LogPath, compilation.Error);
        }
        finally
        {
            DeleteOwnedStaging(parent, staging);
        }
    }

    private static async Task CopyVerifiedAsync(CopyInput file, string destination, CancellationToken token)
    {
        ImportPaths.RejectLinks(file.Source);
        await using var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long bytes = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            bytes += count;
            if (bytes > file.Bytes)
            {
                throw new InvalidOperationException("文件在预览后增长，请重新预览：" + file.RelativePath);
            }
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (bytes != file.Bytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
        {
            throw new InvalidOperationException("文件在预览后变化，请重新预览：" + file.RelativePath);
        }
    }

    private static void DeleteOwnedStaging(string parent, string staging)
    {
        var full = Path.GetFullPath(staging);
        if (!Path.GetDirectoryName(full)!.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(".studiox-keil-import-", StringComparison.Ordinal) || !Directory.Exists(full))
        {
            return;
        }
        ImportPaths.RejectLinks(full);
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
        {
            ImportPaths.RejectLinks(entry);
        }
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(full, recursive: true);
    }
}
