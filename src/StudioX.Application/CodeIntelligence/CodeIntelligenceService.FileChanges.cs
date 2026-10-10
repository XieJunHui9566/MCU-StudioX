namespace StudioX.Application.CodeIntelligence;

using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    /// <summary>磁盘变化与编辑缓冲区共享诊断修订；普通写入不重启语言进程。</summary>
    public async Task RefreshFilesAsync(ProjectChangeBatch batch, CancellationToken token = default)
    {
        if (!batch.AnalysisChanged)
        {
            return;
        }
        InvalidateDiagnostics();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!string.Equals(analysisDirectory, batch.Directory, StringComparison.OrdinalIgnoreCase) || DiagnosticsSuspended)
            {
                return;
            }
            // 未配置的自动发现数据库必须补充新增和改名后的翻译单元；原生数据库由配置或编译产生。
            var nativeDatabase = analysisProject?.Espressif is not null || analysisProject?.Kind == ProjectKind.CubeMx ||
                File.Exists(PathBoundary.Resolve(batch.Directory, ".studiox/keil-import.json")) ||
                File.Exists(PathBoundary.Resolve(batch.Directory, ".build/compile_commands.json"));
            if (batch.RequiresRescan || batch.NamesChanged && !nativeDatabase)
            {
                await StartCoreAsync(batch.Directory, token).ConfigureAwait(false);
                return;
            }
            await RefreshEnvironmentCoreAsync(token).ConfigureAwait(false);
            if (connection is not null && IsReady)
            {
                var changes = batch.Changes.SelectMany(change => change.Kind == ProjectFileChangeKind.Renamed
                    ? new[] { new { uri = new Uri(ResolveDocumentPath(change.PreviousPath!)).AbsoluteUri, type = 3 },
                        new { uri = new Uri(ResolveDocumentPath(change.Path)).AbsoluteUri, type = 1 } }
                    : new[] { new { uri = new Uri(ResolveDocumentPath(change.Path)).AbsoluteUri,
                        type = change.Kind == ProjectFileChangeKind.Created ? 1 : change.Kind == ProjectFileChangeKind.Deleted ? 3 : 2 } }).ToArray();
                await connection.NotifyAsync("workspace/didChangeWatchedFiles", new
                {
                    changes
                }, token).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
