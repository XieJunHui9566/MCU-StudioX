namespace StudioX.Application;

/// <summary>协调配色与字体的保存；失败回退偏好，不接触背景媒体或播放会话。</summary>
public sealed class WorkbenchStyleService(ThemeService themes, TypographyService typography, EditorSettingsService editor)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task SaveAsync(WorkbenchStyle style, CancellationToken token = default)
    {
        ThemeService.Validate(style.Theme);
        TypographyService.Validate(style.Typography);
        EditorSettingsService.Validate(style.Editor);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = new WorkbenchStyle(await themes.LoadAsync(token).ConfigureAwait(false),
                await typography.LoadAsync(token).ConfigureAwait(false), await editor.LoadAsync(token).ConfigureAwait(false));
            var typographySaved = false;
            var editorSaved = false;
            var themeAttempted = false;
            try
            {
                await typography.SaveAsync(style.Typography, token).ConfigureAwait(false);
                typographySaved = true;
                await editor.SaveAsync(style.Editor, token).ConfigureAwait(false);
                editorSaved = true;
                themeAttempted = true;
                await themes.SelectAsync(style.Theme, token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 回退不能复用已取消的令牌；保留原始保存异常及任何恢复失败。
                var cleanup = new ResourceCleanup();
                if (themeAttempted)
                {
                    // 自定义主题内容先于选择偏好落盘；选择写入失败也要恢复原配色。
                    await cleanup.RunAsync(() => new ValueTask(themes.SelectAsync(previous.Theme))).ConfigureAwait(false);
                }
                if (editorSaved)
                {
                    await cleanup.RunAsync(() => new ValueTask(editor.SaveAsync(previous.Editor))).ConfigureAwait(false);
                }
                if (typographySaved)
                {
                    await cleanup.RunAsync(() => new ValueTask(typography.SaveAsync(previous.Typography))).ConfigureAwait(false);
                }
                try
                {
                    cleanup.ThrowIfFailed("外观偏好回退失败。");
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("外观保存和回退均失败。", error, rollback);
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
