namespace StudioX.Application.Editing;

using System.Security.Cryptography;
using System.Text;
using StudioX.Application.Mcp;
using StudioX.Foundation;

/// <summary>Agent 修改先生成快照计划，审批后原子应用到缓冲区；验证另行保存明确快照。</summary>
public sealed class AgentEditorSession(WorkbenchService services, string project, IAgentEditorAccess editor,
    IStudioXMcpAuthorizer authorizer)
{
    private readonly List<AgentEditPlan> plans = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public string TaskId { get; private set; } = Guid.NewGuid().ToString("N");
    public string TaskTitle { get; private set; } = "编辑任务";
    public string ValidationStatus { get; private set; } = "尚未验证";
    public IReadOnlyList<AgentEditPlan> Plans
    {
        get
        {
            lock (plans)
            {
                return plans.ToArray();
            }
        }
    }
    public event Action? Changed;
    public void BeginTask(string title)
    {
        TaskId = Guid.NewGuid().ToString("N");
        TaskTitle = title.Length <= 160 ? title : title[..160];
        ValidationStatus = "尚未验证";
        Changed?.Invoke();
    }
    public Task<AgentEditorSnapshot> SnapshotAsync(CancellationToken token = default) => editor.SnapshotAsync(token);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public async Task<WorkspaceBufferSnapshot> ReadAsync(string path, CancellationToken token = default)
    {
        _ = new McpWorkspaceAccess(project).RequireWorkspaceSourceFile(path);
        var snapshot = await editor.SnapshotAsync(token);
        var open = snapshot.Documents.FirstOrDefault(d => d.Source.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (open is not null)
        {
            return open;
        }
        var disk = await services.Files.ReadAsync(project, path, token);
        return new(disk, disk.Text);
    }
    public async Task<AgentEditPlan> PlanAsync(IReadOnlyList<AgentTextPatch> patches, string reason, CancellationToken token = default)
    {
        if (patches is null || patches.Count is < 1 or > 100 || patches.Any(p => p is null) || patches.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != patches.Count)
        {
            throw new StudioXException("AGENT_PLAN_SIZE", "每个计划需要 1–100 个不同文件。");
        }
        var snapshot = await editor.SnapshotAsync(token);
        var changes = new List<WorkspaceFileChange>();
        foreach (var patch in patches)
        {
            _ = new McpWorkspaceAccess(project).RequireWorkspaceWritableSourceFile(patch.Path);
            var open = snapshot.Documents.FirstOrDefault(d => d.Source.RelativePath.Equals(patch.Path, StringComparison.OrdinalIgnoreCase));
            var source = open?.Source ?? await services.Files.ReadAsync(project, patch.Path, token);
            var before = open?.Text ?? source.Text;
            if (source.IsReadOnly || !Hash(before).Equals(patch.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("AGENT_SNAPSHOT_CHANGED", patch.Path + " 内容已变化或只读，请重新读取。");
            }
            if (patch.Hunks is null || patch.Hunks.Count is < 1 or > 100 || patch.Hunks.Any(h => h is null || string.IsNullOrEmpty(h.OldText) || h.NewText is null))
            {
                throw new StudioXException("AGENT_PATCH", "修改块不能为空；创建文件使用 project_create_file。");
            }
            var matches = new List<TextSearchMatch>();
            foreach (var hunk in patch.Hunks)
            {
                var at = before.IndexOf(hunk.OldText, StringComparison.Ordinal);
                if (at < 0 || before.IndexOf(hunk.OldText, at + 1, StringComparison.Ordinal) >= 0)
                {
                    throw new StudioXException("AGENT_PATCH_MATCH", patch.Path + " 的原文不唯一或已变化，请扩大上下文。");
                }
                matches.Add(new(at, hunk.OldText.Length, hunk.NewText));
            }
            var after = WorkspaceEditService.ApplyText(before, matches);
            if (before != after)
            {
                changes.Add(new(source, before, after, matches, open is not null));
            }
        }
        return await StageAsync(changes, reason, token);
    }
    public async Task<AgentEditPlan> StageAsync(IReadOnlyList<WorkspaceFileChange> changes, string reason, CancellationToken token = default, bool isAtomic = false)
    {
        if (changes.Count == 0 || changes.Count > 100 || changes.Sum(c => (long)c.Before.Length + c.After.Length) > 8 * 1024 * 1024)
        {
            throw new StudioXException("AGENT_PLAN_SIZE", "计划为空或超出范围，请缩小修改。");
        }
        foreach (var change in changes)
        {
            _ = new McpWorkspaceAccess(project).RequireWorkspaceWritableSourceFile(change.Path);
        }
        var snapshot = await editor.SnapshotAsync(token);
        await services.WorkspaceEdits.ValidateAsync(project, changes, snapshot.Documents, token);
        reason = string.IsNullOrWhiteSpace(reason) ? "修改代码" : reason;
        var plan = new AgentEditPlan(Guid.NewGuid().ToString("N"), TaskId, reason.Length > 500 ? reason[..500] : reason, changes, isAtomic);
        if (Plans.Count >= 200 || Plans.Sum(p => p.Changes.Sum(c => (long)c.Before.Length + c.After.Length)) + changes.Sum(c => (long)c.Before.Length + c.After.Length) > 32 * 1024 * 1024)
        {
            throw new StudioXException("AGENT_HISTORY_LIMIT", "本次会话的修改记录已达到上限，请保存并重新打开工程。");
        }
        lock (plans)
        {
            plans.Add(plan);
        }
        await editor.PresentAsync(plan, token);
        Changed?.Invoke();
        return plan;
    }
    public async Task<AgentEditPlan> ApplyAsync(string id, CancellationToken token = default, bool userSelectedApply = false)
    {
        await gate.WaitAsync(token);
        try
        {
            var plan = Find(id);
            if (plan.Status != "pending")
            {
                throw new StudioXException("AGENT_PLAN_STATE", "计划已处理，不能重复执行。");
            }
            await editor.PresentAsync(plan, token);
            if (!userSelectedApply && !await authorizer.ApproveAsync(new(project, "editor_apply_plan", $"应用 {plan.Changes.Count} 个文件的修改：{plan.Reason}。请在 AI 修改页审阅和勾选修改块。", StudioXMcpPermission.FileWrite), token))
            {
                plan.Status = "rejected";
                Changed?.Invoke();
                throw new StudioXException("MCP_APPROVAL_DENIED", "用户拒绝了修改计划。");
            }
            token.ThrowIfCancellationRequested();
            var selected = await editor.SelectedAsync(plan, token);
            if (selected.Count == 0)
            {
                plan.Status = "rejected";
                Changed?.Invoke();
                return plan;
            }
            if (plan.IsAtomic && (selected.Count != plan.Changes.Count ||
                selected.Any(c => !plan.Changes.Any(p => p.Path == c.Path && p.After == c.After))))
            {
                throw new StudioXException("AGENT_ATOMIC_PLAN", "语义重命名必须整体应用，不能仅应用部分文件或修改块。");
            }
            await editor.ApplyAsync(selected, token);
            plan.Changes = selected;
            plan.Status = "applied";
            ValidationStatus = "代码已修改，尚未验证";
            Changed?.Invoke();
            return plan;
        }
        finally { gate.Release(); }
    }
    public async Task UndoAsync(string id, CancellationToken token = default)
        => await UndoPlansAsync([Find(id)], token);
    public async Task UndoTaskAsync(string taskId, CancellationToken token = default)
        => await UndoPlansAsync(Plans.Where(p => p.TaskId == taskId && p.Status == "applied").Reverse().ToArray(), token);
    private async Task UndoPlansAsync(IReadOnlyList<AgentEditPlan> undoPlans, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (undoPlans.Count == 0 || undoPlans.Any(p => p.Status != "applied"))
            {
                throw new StudioXException("AGENT_PLAN_STATE", "只能撤销已应用的修改。");
            }
            // 先在内存中逆序回放全部计划；任何冲突都不会留下半个任务的撤销结果。
            var originals = new Dictionary<string, WorkspaceBufferSnapshot>(StringComparer.OrdinalIgnoreCase);
            var restoredTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in undoPlans)
            {
                foreach (var change in plan.Changes)
                {
                    if (!originals.ContainsKey(change.Path))
                    {
                        var current = await ReadAsync(change.Path, token);
                        originals.Add(change.Path, current);
                        restoredTexts.Add(change.Path, current.Text);
                    }
                    restoredTexts[change.Path] = Reverse(change, restoredTexts[change.Path]);
                }
            }
            var snapshot = await editor.SnapshotAsync(token);
            var reversals = new List<WorkspaceFileChange>();
            foreach (var (path, current) in originals)
            {
                var text = current.Text;
                var restored = restoredTexts[path];
                if (text != restored)
                {
                    reversals.Add(new(current.Source, text, restored, [new(0, text.Length, restored)],
                        snapshot.Documents.Any(d => d.Source.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))));
                }
            }
            if (reversals.Count > 0)
            {
                await editor.ApplyAsync(reversals, token);
            }
            foreach (var plan in undoPlans)
            {
                plan.Status = "reverted";
            }
            ValidationStatus = "已撤销修改，原验证结果已过期";
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task SaveForBuildAsync(CancellationToken token = default)
    {
        var snapshot = await editor.SnapshotAsync(token);
        var dirty = snapshot.Documents.Where(d => d.Text != d.Source.Text || d.Source.IsMissing).ToArray();
        if (dirty.Length == 0)
        {
            return;
        }
        if (!await authorizer.ApproveAsync(new(project, "editor_save_for_build", $"保存当前 {dirty.Length} 个未保存文件以验证当前代码（包含用户编辑）：{string.Join(", ", dirty.Select(d => d.Source.RelativePath))}", StudioXMcpPermission.FileWrite), token))
        {
            throw new StudioXException("MCP_APPROVAL_DENIED", "未保存当前快照，未启动编译。");
        }
        token.ThrowIfCancellationRequested();
        await editor.SaveAsync(dirty, token);
    }
    public void RecordValidation(bool success, bool stale)
    {
        ValidationStatus = stale ? "编译期间编辑内容变化，验证结果已过期" : success ? "编译通过 · 尚未实板验证" : "编译失败 · 查看原始诊断";
        Changed?.Invoke();
    }
    public void InvalidateValidation()
    {
        if (ValidationStatus.StartsWith("编译通过", StringComparison.Ordinal))
        {
            ValidationStatus = "编译后代码已有修改，验证结果已过期";
            Changed?.Invoke();
        }
    }
    private AgentEditPlan Find(string id) => Plans.FirstOrDefault(p => p.Id == id) ?? throw new StudioXException("AGENT_PLAN_MISSING", "修改计划不属于当前会话。");
    private static string Reverse(WorkspaceFileChange change, string current)
    {
        if (current == change.After)
        {
            return change.Before;
        }
        var inverse = new List<TextSearchMatch>();
        var delta = 0;
        var positions = change.Matches.OrderBy(m => m.Start).Select(m => { var at = m.Start + delta; delta += m.Replacement.Length - m.Length; return (Match: m, At: at); }).ToArray();
        foreach (var (edit, at) in positions.Reverse())
        {
            var left = change.After.Substring(Math.Max(0, at - 32), Math.Min(32, at));
            var rightStart = at + edit.Replacement.Length;
            var right = change.After.Substring(rightStart, Math.Min(32, change.After.Length - rightStart));
            var context = left + edit.Replacement + right;
            var found = current.IndexOf(context, StringComparison.Ordinal);
            if (found < 0 || context.Length == 0 || current.IndexOf(context, found + 1, StringComparison.Ordinal) >= 0)
            {
                throw new StudioXException("AGENT_UNDO_CONFLICT", change.Path + " 的修改区域已有后续编辑，保留当前内容；请逐块核对。");
            }
            inverse.Add(new(found + left.Length, edit.Replacement.Length, change.Before.Substring(edit.Start, edit.Length)));
        }
        return WorkspaceEditService.ApplyText(current, inverse);
    }
}
