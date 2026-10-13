namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using StudioX.Application;

/// <summary>工作台呈现与输入；工程事务、工具与平台能力始终交给应用服务。</summary>
internal sealed partial class MainWindow : Window
{
    private readonly StartupOptions options;
    private readonly PortableWorkspaceService workspace;
    private readonly TextEditor editor = new()
    {
        ShowLineNumbers = true,
        FontFamily = new FontFamily("Cascadia Mono,DejaVu Sans Mono,Consolas"),
        FontSize = 14,
        Padding = new Thickness(8),
        IsReadOnly = true
    };
    private readonly TreeView tree = new();
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("DejaVu Sans Mono,Consolas"), FontSize = 12 };
    private readonly TextBlock status = new() { Text = "就绪", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock projectLabel = new() { Text = "未打开工程", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock projectTitle = new() { Text = "欢迎" };
    private readonly TextBlock documentLabel = new() { Text = "", FontSize = 11 };
    private readonly Button openButton = new();
    private readonly Button saveButton = new();
    private readonly Button buildButton = new();
    private readonly Button cancelButton = new();
    private readonly Button closeProjectButton = new();
    private readonly List<EditorBuffer> buffers = [];
    private EditorBuffer? activeBuffer;
    private PortableDocument? document => activeBuffer?.Snapshot;
    private bool dirty => activeBuffer?.IsDirty == true;
    private bool loadingDocument;
    private bool busy;
    private bool closing;
    private bool allowClose;
    private CancellationTokenSource? operationCancellation;
    private Task operation = Task.CompletedTask;
    private readonly SearchPanel searchPanel;

    public MainWindow(StartupOptions options)
    {
        this.options = options;
        workspace = new(options.RuntimeDirectory, options.DataDirectory);
        Title = "MCU StudioX";
        Width = 1460;
        Height = 920;
        MinWidth = 1100;
        MinHeight = 720;
        FontSize = 12;
        FontFamily = new FontFamily("Noto Sans CJK SC,Microsoft YaHei UI,Arial");
        WindowDecorations = WindowDecorations.None;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://StudioX.PortableDesktop/Assets/StudioX.ico")));
        Content = CreateWorkbench();
        ApplyTheme(ThemeService.Dark);
        searchPanel = SearchPanel.Install(editor);
        openButton.Click += (_, _) => RunOperationAsync(PickProjectAsync);
        saveButton.Click += (_, _) => RunOperationAsync(SaveDocumentAsync);
        buildButton.Click += (_, _) => RunOperationAsync(BuildProjectAsync);
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        closeProjectButton.Click += (_, _) => RunOperationAsync(CloseProjectAsync);
        tree.DoubleTapped += (_, _) => RunOperationAsync(OpenSelectedFileAsync);
        editor.TextChanged += (_, _) =>
        {
            if (!loadingDocument && activeBuffer is not null)
            {
                UpdateControls();
            }
        };
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition();
        KeyDown += OnKeyDown;
        Opened += (_, _) => RunOperationAsync(async token =>
        {
            ApplyTheme(await workspace.LoadThemeAsync(token));
            await RefreshWelcomeAsync(token);
            if (options.ProjectDirectory is not null)
            {
                await OpenProjectAsync(options.ProjectDirectory, token);
            }
            if (options.ValidationDirectory is not null)
            {
                await ValidateWorkflowAsync(options.ValidationDirectory, token);
            }
        });
        Closing += OnClosing;
        UpdateControls();
    }

    private void RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (busy || closing)
        {
            return;
        }
        busy = true;
        operationCancellation = new();
        UpdateControls();
        operation = ExecuteAsync(action, operationCancellation);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation)
    {
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status.Text = "操作已取消";
            AppendLog("操作已取消。");
        }
        catch (Exception error)
        {
            status.Text = "操作失败：" + error.Message;
            AppendLog(error.ToString());
            ShowOutput(true);
            if (options.ValidationDirectory is not null)
            {
                WriteValidationFailure(error);
                Environment.ExitCode = 1;
                await workspace.DisposeAsync();
                allowClose = true;
                Dispatcher.UIThread.Post(Close);
            }
        }
        finally
        {
            cancellation.Dispose();
            operationCancellation = null;
            busy = false;
            UpdateControls();
        }
    }

    private void AppendLog(string text)
    {
        // 窗口只保留有限显示缓冲；完整工具诊断由 BuildService 写入工程日志。
        var updated = log.Text + text + "\n";
        log.Text = updated.Length > 60000 ? "[较早输出请查看工程构建日志]\n" + updated[^50000..] : updated;
        log.CaretIndex = log.Text.Length;
    }

    private void UpdateControls()
    {
        openButton.IsEnabled = !busy && !closing;
        saveButton.IsEnabled = !busy && !closing && dirty && document?.Source.IsReadOnly == false;
        buildButton.IsEnabled = !busy && !closing && workspace.Project is not null;
        cancelButton.IsEnabled = busy && !closing;
        closeProjectButton.IsEnabled = !busy && !closing && workspace.Project is not null;
        tree.IsEnabled = !busy && !closing;
        editor.IsReadOnly = busy || closing || document is null || document.Source.IsReadOnly;
        foreach (var buffer in buffers)
        {
            buffer.Label.Text = Path.GetFileName(buffer.Snapshot.Source.RelativePath) + (buffer.IsDirty ? " *" : "") + (buffer.Snapshot.Source.IsReadOnly ? " · 只读" : "");
        }
        projectTitle.Text = workspace.Project?.Name ?? "欢迎";
        projectTarget.Text = workspace.Project?.Device ?? "无构建目标";
        projectLabel.Text = workspace.Project is { } current ? current.Name + "\n" + current.Device + " · " + current.Template : "尚未打开工程\n创建一个工程，或从本地打开已有工程。";
        Title = "MCU StudioX" + (workspace.Project is { } project ? " — " + project.Name : "") + (buffers.Any(buffer => buffer.IsDirty) ? " *" : "");
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        documentLabel.Text = document is null ? "" : document.Source.RelativePath + "   " + editor.TextArea.Caret.Line + ":" + editor.TextArea.Caret.Column + "   UTF-8";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.O:
                    RunOperationAsync(PickProjectAsync);
                    break;
                case Key.N:
                    RunOperationAsync(NewProjectAsync);
                    break;
                case Key.S:
                    RunOperationAsync(e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? ShowAppearanceAsync
                        : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SaveAllAsync : SaveDocumentAsync);
                    break;
                case Key.W:
                    RunOperationAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? CloseProjectAsync : CloseActiveAsync);
                    break;
                case Key.Tab:
                    CycleDocument(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.D1)
        {
            ToggleProject();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.D4)
        {
            ShowOutput(!outputVisible);
            e.Handled = true;
        }
        else if (e.Key == Key.F7)
        {
            RunOperationAsync(BuildProjectAsync);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && tree.IsKeyboardFocusWithin)
        {
            RunOperationAsync(RefreshTreeAsync);
            e.Handled = true;
        }
    }
}
