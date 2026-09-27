namespace StudioX.Desktop;

/// <summary>窗口仅转发 UI 事件；活动气泡及其缓冲区由独立视图对象拥有。</summary>
public partial class MainWindow
{
    private AiActivityPresenter? aiActivityPresenter;

    private AiActivityPresenter AiActivity => aiActivityPresenter ??= new(
        AiTranscriptItems, AiTranscript, UpdateAiEmptyHint, OnAiToolCompletedForEditor);

    private void StartAiActivity() => AiActivity.Start();
    private void DrainAiActivityProgress() => aiActivityPresenter?.DrainProgress();
    private void SetAiActivityStatus(string status) => aiActivityPresenter?.SetStatus(status);
    private void FinishAiActivity() => aiActivityPresenter?.Finish();
    private void RetainInterruptedAiActivity(string status) => aiActivityPresenter?.RetainInterrupted(status);
    private void ClearAiAnswerPreview() => aiActivityPresenter?.ClearAnswerPreview();
}
