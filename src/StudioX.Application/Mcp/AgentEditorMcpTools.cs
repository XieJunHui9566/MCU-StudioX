namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;
using StudioX.Foundation;

/// <summary>对编辑器实时快照执行检索、语义分析与可审阅修改。</summary>
internal sealed class AgentEditorMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    private AgentEditorSession Editor => Context.AgentEditor ?? throw new StudioXException("AGENT_EDITOR_UNAVAILABLE", "会话没有编辑器访问。");
    private static string Json(object value) => JsonSerializer.Serialize(value, JsonStore.Options);
    private static object PlanReceipt(AgentEditPlan plan) => new { plan.Id, plan.TaskId, plan.Reason, plan.Status, files = plan.Changes.Select(c => new { c.Path, hunks = c.Matches.Count, contentHash = AgentEditorSession.Hash(c.After) }), saved = false };

    [McpServerTool(Name = "editor_context")]
    [Description("读取当前活动文件、光标、选区和打开文档元信息，包含未保存内容的哈希；选区正文最多 8000 字符。")]
    public async Task<string> ContextAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await Editor.SnapshotAsync(cancellationToken);
        var documents = snapshot.Documents.Where(d => McpWorkspacePathPolicy.IsWorkspaceSourceFile(d.Source.RelativePath)).ToArray();
        var active = documents.FirstOrDefault(d => d.Source.RelativePath == snapshot.ActivePath);
        var start = Math.Clamp(snapshot.SelectionStart, 0, active?.Text.Length ?? 0);
        var length = Math.Clamp(snapshot.SelectionLength, 0, (active?.Text.Length ?? 0) - start);
        return Json(new
        {
            snapshot.ActivePath,
            snapshot.Caret,
            selectionStart = start,
            selectionLength = length,
            selectedText = active?.Text.Substring(start, Math.Min(length, 8000)),
            documents = documents.Select(d => new { path = d.Source.RelativePath, dirty = d.Text != d.Source.Text, contentHash = AgentEditorSession.Hash(d.Text), length = d.Text.Length }),
            note = "编辑器正文使用 editor_read；修改使用 editor_plan_changes / editor_plan_refactor 再 editor_apply_plan。"
        });
    }

    [McpServerTool(Name = "editor_read")]
    [Description("读取编辑器当前正文，优先未保存缓冲区，未打开文件读取磁盘。offset 是 UTF-16 字符偏移；contentHash 用于修改计划。")]
    public async Task<string> ReadAsync(string path, int offset = 0, int maxChars = 12000, CancellationToken cancellationToken = default)
    {
        var buffer = await Editor.ReadAsync(path, cancellationToken);
        if (offset < 0 || offset > buffer.Text.Length || maxChars is < 1 or > 16000)
        {
            throw new StudioXException("AGENT_READ_RANGE", "正文读取范围无效。");
        }
        var count = Math.Min(maxChars, buffer.Text.Length - offset);
        return Json(new
        {
            path = buffer.Source.RelativePath,
            contentHash = AgentEditorSession.Hash(buffer.Text),
            diskHash = buffer.Source.DiskHash,
            dirty = buffer.Text != buffer.Source.Text,
            readOnly = buffer.Source.IsReadOnly,
            text = buffer.Text.Substring(offset, count),
            offset,
            nextOffset = offset + count,
            endOfFile = offset + count == buffer.Text.Length,
            totalChars = buffer.Text.Length
        });
    }

    [McpServerTool(Name = "editor_search")]
    [Description("在工程与当前未保存代码中搜索；返回有界匹配位置，不自动修改。支持文件包含规则，例如 src/**;*.h。")]
    public async Task<string> SearchAsync(string query, string include = "", bool matchCase = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await Editor.SnapshotAsync(cancellationToken);
        var result = await Services.WorkspaceEdits.SearchAsync(Project, new(query, matchCase, false, false), null, include, "", false, snapshot.Documents, cancellationToken);
        var files = result.Files.Where(f => McpWorkspacePathPolicy.IsWorkspaceSourceFile(f.Path)).ToArray();
        return Json(new
        {
            result.ScannedFiles,
            result.Notices,
            results = files.Take(100).Select(f => new { f.Path, contentHash = AgentEditorSession.Hash(f.Before), matches = f.Matches.Take(30).Select(m => new { m.Start, m.Length, preview = f.Before.Substring(Math.Max(0, m.Start - 60), Math.Min(180, f.Before.Length - Math.Max(0, m.Start - 60))) }) }),
            truncated = files.Length > 100 || files.Any(f => f.Matches.Count > 30)
        });
    }

    [McpServerTool(Name = "editor_inspect")]
    [Description("使用当前缓冲区的 C/C++ 语义服务查询 definition、references 或 diagnostics；offset 为 UTF-16 偏移。诊断只返回与当前快照匹配的结果。")]
    public async Task<string> InspectAsync(string path, string action, int offset = 0, CancellationToken cancellationToken = default)
    {
        var buffer = await Editor.ReadAsync(path, cancellationToken);
        var snapshot = await Editor.SnapshotAsync(cancellationToken);
        var documents = snapshot.Documents.Select(d => new CodeDocumentSnapshot(d.Source.RelativePath, d.Text)).ToArray();
        if (!Services.Intelligence.IsReady || !CodeIntelligenceService.Supports(path))
        {
            throw new StudioXException("AGENT_LANGUAGE", "当前文件没有可用的 C/C++ 语义服务。");
        }
        if (offset < 0 || offset > buffer.Text.Length)
        {
            throw new StudioXException("AGENT_OFFSET", "字符偏移超出正文。");
        }
        if (action == "definition")
        {
            return Json(await Services.Intelligence.NavigateAsync(path, buffer.Text, offset, false, cancellationToken, documents));
        }
        if (action == "references")
        {
            return Json(await Services.Intelligence.ReferencesAsync(path, buffer.Text, offset, documents, cancellationToken));
        }
        if (action != "diagnostics")
        {
            throw new StudioXException("AGENT_ACTION", "支持 definition、references、diagnostics。");
        }
        await Services.Intelligence.SynchronizeDiagnosticsAsync(path, buffer.Text, documents, cancellationToken);
        CodeDiagnosticBatch? batch = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            batch = Services.Intelligence.GetDiagnostics().FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && d.Text == buffer.Text);
            if (batch is not null)
            {
                break;
            }
            await Task.Delay(100, cancellationToken);
        }
        return Json(new
        {
            path,
            contentHash = AgentEditorSession.Hash(buffer.Text),
            pending = batch is null,
            diagnostics = batch?.Items,
            evidence = "实时静态分析；不代表编译或实板通过"
        });
    }

    [McpServerTool(Name = "editor_plan_changes")]
    [Description("对一个或多个文件生成修改预览；每个文件提供 editor_read 的 contentHash 和唯一匹配 oldText/newText 块。不执行或保存，返回计划 id。")]
    public async Task<string> PlanAsync(AgentTextPatch[] files, string reason, CancellationToken cancellationToken = default) => Json(PlanReceipt(await Editor.PlanAsync(files, reason, cancellationToken)));

    [McpServerTool(Name = "editor_plan_refactor")]
    [Description("调用 C/C++ 语义服务生成 rename、format 或 fix 计划。rename 需要当前位置的 oldName/newName；fix 的 fixIndex 默认 0。只生成预览，不应用。")]
    public async Task<string> RefactorAsync(string path, string action, int offset = 0, string oldName = "", string newName = "", int fixIndex = 0, CancellationToken cancellationToken = default)
    {
        var buffer = await Editor.ReadAsync(path, cancellationToken);
        var snapshot = await Editor.SnapshotAsync(cancellationToken);
        var documents = snapshot.Documents.Select(d => new CodeDocumentSnapshot(d.Source.RelativePath, d.Text)).ToArray();
        if (!Services.Intelligence.IsReady || !CodeIntelligenceService.Supports(path))
        {
            throw new StudioXException("AGENT_LANGUAGE", "当前文件没有可用的 C/C++ 语义服务。");
        }
        IReadOnlyList<WorkspaceFileChange> changes;
        if (action == "rename")
        {
            changes = await Services.Intelligence.RenameAsync(path, buffer.Text, offset, oldName, newName, snapshot.Documents, cancellationToken);
        }
        else if (action == "format")
        {
            changes = await Services.Intelligence.FormatAsync(buffer, null, 0, documents, cancellationToken);
        }
        else if (action == "fix")
        {
            var fixes = await Services.Intelligence.QuickFixesAsync(buffer, offset, documents, cancellationToken);
            if (fixIndex < 0 || fixIndex >= fixes.Count)
            {
                return Json(new
                {
                    available = fixes.Select((f, i) => new { index = i, f.Title }),
                    message = "没有该索引的直接文本修复。"
                });
            }
            changes = fixes[fixIndex].Changes;
        }
        else
        {
            throw new StudioXException("AGENT_ACTION", "支持 rename、format、fix。");
        }
        return Json(PlanReceipt(await Editor.StageAsync(changes, action + " " + path, cancellationToken, isAtomic: action == "rename")));
    }

    [McpServerTool(Name = "editor_apply_plan")]
    [Description("按当前授权模式应用已生成的修改计划。用户可勾选修改块；应用到未保存缓冲区，编译通过 project_build 保存并验证。")]
    public async Task<string> ApplyAsync(string planId, CancellationToken cancellationToken = default) => Json(PlanReceipt(await Editor.ApplyAsync(planId, cancellationToken)));

    [McpServerTool(Name = "editor_task_status")]
    [Description("读取当前任务的修改记录和验证状态，区分缓冲区修改、编译结果与尚未实板验证。")]
    public string Status() => Json(new { Editor.TaskId, Editor.TaskTitle, Editor.ValidationStatus, plans = Editor.Plans.Where(p => p.TaskId == Editor.TaskId).Select(PlanReceipt) });
}
