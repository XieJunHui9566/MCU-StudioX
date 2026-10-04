namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Onboarding;

public partial class FirstProjectGuideView : UserControl
{
    private IReadOnlyList<FirstProjectStep> steps = [];
    private bool updating;
    public Func<string, Task>? ActionRequested
    {
        get; init;
    }
    public Func<string, Task>? HelpRequested
    {
        get; init;
    }
    public Func<Task>? FinishRequested
    {
        get; init;
    }
    public Action<Exception>? Failed
    {
        get; init;
    }
    public int SelectedStep => Math.Max(0, StepList.SelectedIndex);
    public FirstProjectGuideView() => InitializeComponent();
    public void Refresh(FirstProjectProgress progress)
    {
        var selected = SelectedStep;
        steps = FirstProjectGuideService.Steps(progress);
        updating = true;
        StepList.ItemsSource = steps.Select((step, index) => $"{(step.Complete ? "✓" : (index + 1).ToString())}  {step.Title}").ToArray();
        StepList.SelectedIndex = selected;
        updating = false;
        ContextText.Text = progress.ProjectDirectory is null ? "尚未打开工程 · 可从第 1 步开始" : "当前工程：" + progress.ProjectName + " · " + progress.Target;
        Present();
    }
    public void Select(int index) => StepList.SelectedIndex = Math.Clamp(index, 0, steps.Count - 1);
    private void Present()
    {
        if (steps.Count == 0)
        {
            return;
        }
        var step = steps[SelectedStep];
        StepTitle.Text = $"{SelectedStep + 1}. {step.Title}";
        InstructionsText.Text = step.Instructions;
        ExpectedText.Text = step.Expected;
        EvidenceText.Text = step.Status;
        ActionButton.Content = step.ActionTitle;
        ActionButton.IsEnabled = step.CanAct;
        SecondaryButton.Visibility = SelectedStep is 1 or 3 ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Content = SelectedStep == 1 ? "打开已有工程…" : "保存全部文件";
        SecondaryButton.IsEnabled = SelectedStep == 1 || step.CanAct;
        BackButton.IsEnabled = SelectedStep > 0;
        NextButton.IsEnabled = SelectedStep < steps.Count - 1;
        ProgressText.Text = $"第 {SelectedStep + 1} / {steps.Count} 步 · 可以回看或跳过阅读；完成标记依据实际操作";
    }
    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!updating)
        {
            Present();
            BodyScroll.ScrollToTop();
        }
    }
    private void Back_Click(object sender, RoutedEventArgs e) => Select(SelectedStep - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Select(SelectedStep + 1);
    private async void Action_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => ActionRequested?.Invoke(steps[SelectedStep].Action) ?? Task.CompletedTask);
    private async void Secondary_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => ActionRequested?.Invoke(SelectedStep == 1 ? "open" : "save") ?? Task.CompletedTask);
    private async void Help_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => HelpRequested?.Invoke(steps[SelectedStep].HelpTopic) ?? Task.CompletedTask);
    private async void Finish_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => FinishRequested?.Invoke() ?? Task.CompletedTask);
    private async Task ExecuteAsync(Func<Task> action)
    {
        Actions.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error) { EvidenceText.Text = error.Message; Failed?.Invoke(error); }
        finally { Actions.IsEnabled = true; }
    }
}
