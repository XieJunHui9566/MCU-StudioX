namespace StudioX.Desktop;

using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private ProjectChangeSession? projectChangeSession;
    private CancellationTokenSource? projectSyncCancellation;
    private Task projectSyncTask = Task.CompletedTask;
    private readonly SemaphoreSlim projectSyncGate = new(1, 1);
    private event Action<ProjectChangeBatch>? ProjectFilesSynchronized;

    private void StartProjectSynchronization(string directory)
    {
        ResetSourceRegistration();
        var cancellation = new CancellationTokenSource();
        try
        {
            var session = services.ProjectChanges.Watch(directory);
            projectChangeSession = session;
            projectSyncCancellation = cancellation;
            session.Changed += batch =>
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrentProjectSynchronization(session) || cancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        ProjectFilesSynchronized?.Invoke(batch);
                        projectSyncTask = ApplyObservedProjectChangesAsync(projectSyncTask, session, batch, cancellation.Token);
                    });
                }
            };
        }
        catch (Exception error)
        {
            cancellation.Dispose();
            Log("工程自动同步无法启动；可按 F5 重新核对：" + error);
        }
    }

    private bool IsCurrentProjectSynchronization(ProjectChangeSession session) => !closing && !closed &&
        ReferenceEquals(projectChangeSession, session) && string.Equals(projectDirectory, session.Directory, StringComparison.OrdinalIgnoreCase);

    private async Task ApplyObservedProjectChangesAsync(Task previous, ProjectChangeSession session, ProjectChangeBatch batch, CancellationToken token)
    {
        try
        {
            await previous.WaitAsync(token);
            // 外部通知在工程命令完成后合并进界面，避免重建进行中的树或改变保存基线。
            await pendingOperation.WaitAsync(token);
            if (IsCurrentProjectSynchronization(session))
            {
                await SynchronizeProjectFilesAsync(batch, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Log("工程文件自动同步失败；F5 可重试：" + error);
        }
    }

    private async Task StopProjectSynchronizationAsync()
    {
        var cancellation = projectSyncCancellation;
        projectChangeSession?.Dispose();
        projectChangeSession = null;
        projectSyncCancellation = null;
        cancellation?.Cancel();
        await projectSyncTask;
        cancellation?.Dispose();
        projectSyncTask = Task.CompletedTask;
        ResetSourceRegistration();
    }

    private Task<bool> ResynchronizeProjectFilesAsync(CancellationToken token = default) => projectDirectory is { } project
        ? SynchronizeProjectFilesAsync(new(project, 0, [], RequiresRescan: true), token) : Task.FromResult(false);

    private async Task<bool> SynchronizeProjectFilesAsync(ProjectChangeBatch batch, CancellationToken token = default)
    {
        await projectSyncGate.WaitAsync(token);
        try
        {
            var session = projectChangeSession;
            bool Current() => !closing && !closed && projectDirectory == batch.Directory && ReferenceEquals(projectChangeSession, session);
            if (!Current())
            {
                return false;
            }
            ObserveSourceRegistrationChanges(batch);
            if (batch.Diagnostic is not null)
            {
                Log("工程通知不完整，正在重新核对：" + batch.Diagnostic);
            }
            if (batch.AnalysisChanged)
            {
                services.Intelligence.InvalidateDiagnostics();
                liveDiagnosticCancellation?.Cancel();
                CloseCodeAssistance();
                agentEditorSession?.InvalidateValidation();
            }
            foreach (var change in batch.Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed))
            {
                RemapProjectDocuments(change.PreviousPath!, change.Path);
            }
            var needsReview = false;
            bool BuildInput(string path) => !path.StartsWith(".build/", StringComparison.OrdinalIgnoreCase) && ProjectChangePolicy.IsBuildInput(path);
            var invalidateBuild = batch.RequiresRescan || batch.Changes.Any(change => BuildInput(change.Path) && FindEditor(change.Path) is null ||
                change.PreviousPath is { } previous && BuildInput(previous));
            foreach (var editor in editorDocuments.ToArray())
            {
                var path = editor.Source.RelativePath;
                if (!batch.RequiresRescan && !batch.Changes.Any(change => ProjectFileService.ContainsPath(change.Path, path) ||
                    change.PreviousPath is { } previous && ProjectFileService.ContainsPath(previous, path)))
                {
                    continue;
                }
                try
                {
                    var disk = await services.Files.ReadAsync(batch.Directory, path, token);
                    if (!Current())
                    {
                        return needsReview;
                    }
                    projectChangeSession?.TrackPath(path);
                    if (editorDocuments.Contains(editor) && editor.Source.RelativePath == path &&
                        disk.DiskHash != editor.Source.DiskHash && BuildInput(path))
                    {
                        invalidateBuild = true;
                    }
                    if (editorDocuments.Contains(editor) && editor.Source.RelativePath == path)
                    {
                        var result = EditorSynchronizer.Apply(editor, disk);
                        if (result == EditorDiskSyncResult.UnsavedChangesPreserved)
                        {
                            needsReview = true;
                            Log(path + "：" + editor.DiskConflict);
                        }
                        else if (result == EditorDiskSyncResult.Updated)
                        {
                            QueuePluginDocumentEvent("document.changed", editor);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (!Current())
                    {
                        return needsReview;
                    }
                    var missing = error is FileNotFoundException or DirectoryNotFoundException;
                    invalidateBuild |= BuildInput(path);
                    EditorSynchronizer.PreserveUnavailable(editor, missing,
                        missing ? "磁盘文件已删除，编辑内容保留；保存可重新创建，重现的文件不会被覆盖。" : "磁盘文件无法读取，编辑内容和保存基线已保留：" + error.Message);
                    Log(path + " 同步失败：" + error);
                    needsReview = true;
                }
            }
            if (!Current())
            {
                return needsReview;
            }
            if (invalidateBuild)
            {
                ClearBuildDiagnostics();
                CancelBuildMemoryRefresh();
                BuildMemory.SetMessage("工程输入已变化，重新编译后更新占用。");
                guideBuild = null;
                RefreshFirstProjectGuide();
            }
            if (batch.NamesChanged)
            {
                await SynchronizeProjectTreeAsync(batch, token);
            }
            if (!Current())
            {
                return needsReview;
            }
            try
            {
                await services.Intelligence.RefreshFilesAsync(batch, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { await RecordAnalysisFailureAsync("工程文件已同步，语言服务需要重试", error); }
            if (Current())
            {
                RefreshActiveEditorMetadata();
                RefreshDiagnosticMarkers();
                QueueLiveDiagnostics();
                QueueOutlineRefresh(clear: true);
                QueueEditorCheckpoint();
                CommandManager.InvalidateRequerySuggested();
                if (batch.Revision == 0)
                {
                    ProjectFilesSynchronized?.Invoke(batch);
                }
                if (needsReview)
                {
                    Status.Text = "工程文件已变化；未保存、已删除或无法读取的文档内容已保留，请核对带标记的标签。";
                }
                else if (batch.Changes.Any(change => Path.GetFileName(change.Path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(change.Path).Equals(".cmake", StringComparison.OrdinalIgnoreCase)))
                {
                    Status.Text = "构建文件已变化；请配置或编译工程，更新实际编译参数。";
                }
            }
            return needsReview;
        }
        finally
        {
            projectSyncGate.Release();
        }
    }

    private void RemapProjectDocuments(string previous, string next)
    {
        string Remap(string path) => ProjectFileService.ContainsPath(previous, path) ? next + path[previous.Length..] : path;
        var affected = editorDocuments.Where(editor => ProjectFileService.ContainsPath(previous, editor.Source.RelativePath)).ToArray();
        if (affected.Any(editor => editorDocuments.Any(other => !affected.Contains(other) &&
            other.Source.RelativePath.Equals(Remap(editor.Source.RelativePath), StringComparison.OrdinalIgnoreCase))))
        {
            Log("磁盘改名目标已有打开的标签，保留两份编辑内容并等待核对：" + previous + " → " + next);
            return;
        }
        CaptureEditorView();
        foreach (var editor in affected)
        {
            editor.Source = editor.Source with
            {
                RelativePath = Remap(editor.Source.RelativePath)
            };
            if (editor.Tab.Header is StackPanel { Children.Count: > 0 } row && row.Children[0] is StackPanel { Children.Count: > 0 } label && label.Children[0] is FileIcon icon)
            {
                label.Children.RemoveAt(0);
                label.Children.Insert(0, new FileIcon
                {
                    FileName = editor.Source.RelativePath,
                    Width = icon.Width,
                    Height = icon.Height,
                    Margin = icon.Margin,
                    VerticalAlignment = icon.VerticalAlignment
                });
            }
        }
        for (var i = 0; i < navigationBack.Count; i++)
        {
            navigationBack[i] = navigationBack[i] with
            {
                Path = Remap(navigationBack[i].Path)
            };
        }
        for (var i = 0; i < navigationForward.Count; i++)
        {
            navigationForward[i] = navigationForward[i] with
            {
                Path = Remap(navigationForward[i].Path)
            };
        }
        UpdateEditorHeaders();
        RefreshActiveEditorMetadata();
    }
}
