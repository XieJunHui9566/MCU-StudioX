namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Lvgl;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;

public partial class LvglPreviewSetupView : UserControl
{
    private LvglPreviewService? service;
    private string? project;
    private LvglPreviewConfiguration? existing;
    private LvglUiInspection? inspection;
    private readonly List<SourceChoice> sources = [];
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;
    private int revision;
    private bool busy, shutdown, initializing = true, validated;
    public Action? Closed
    {
        get; set;
    }
    public Action<bool>? BusyChanged
    {
        get; set;
    }
    public Action<string>? LogDiagnostic
    {
        get; set;
    }
    public Func<LvglPreviewConfiguration, bool, CancellationToken, Task>? SavedAsync
    {
        get; set;
    }
    internal int CandidateCount => LibraryCandidates.Items.Count;
    internal int SourceCount => sources.Count;
    internal bool HasValidSetup => validated;

    private sealed record LibraryChoice(LvglLibraryCandidate Candidate)
    {
        public string Directory => Candidate.Directory;
        public string Label => $"LVGL {Candidate.Version ?? "版本未识别"} · {(Candidate.IsSupported && Candidate.IsComplete ? Path.IsPathRooted(Candidate.Directory) ? "需导入工程同盘" : "可用于预览" : Candidate.IsComplete ? "版本尚未支持" : "库文件不完整")}";
        public string Detail => Candidate.MissingFiles.Count > 0 ? "缺少：" + string.Join("、", Candidate.MissingFiles) :
            "版本头 SHA-256：" + Candidate.HeaderSha256;
    }
    private sealed record EntryChoice(LvglUiEntryPoint Entry)
    {
        public string Label => Entry.Name + " · " + Entry.SourceFile + (Entry.NeedsCLinkage ? " · 需要 extern C" : "");
    }
    private sealed record DiagnosticChoice(LvglDiagnostic Diagnostic)
    {
        public string Severity => Diagnostic.Severity;
        public string? Path => Diagnostic.Path;
        public string Summary => (Severity switch
        {
            "error" => "错误",
            "warning" => "警告",
            _ => "提示"
        }) +
            " · " + Diagnostic.Code + " · " + Diagnostic.Message;
    }
    private sealed class SourceChoice(string path, bool selected, string detail)
    {
        public string Path { get; } = path;
        public bool Selected { get; set; } = selected;
        public string Detail { get; } = detail;
    }

    public LvglPreviewSetupView()
    {
        InitializeComponent();
        ColorDepthPicker.ItemsSource = new[] { 16, 32 };
        ColorDepthPicker.SelectedItem = 16;
        ConfigurationHeaderInput.TextChanged += SetupEdited;
        EntryPointInput.TextChanged += SetupEdited;
        UiDirectoryInput.TextChanged += UiDirectoryEdited;
        LibrarySearchInput.TextChanged += LibrarySearchEdited;
        initializing = false;
        UpdateButtons();
    }

    public void Attach(LvglPreviewService source) => service = source;

    public void SetProject(string? directory, LvglPreviewConfiguration? configuration)
    {
        cancellation?.Cancel();
        revision++;
        initializing = true;
        project = directory;
        existing = configuration;
        inspection = null;
        validated = false;
        LibrarySearchInput.Text = configuration?.LvglDirectory ?? ".";
        UiDirectoryInput.Text = configuration?.UiDirectory ?? ".";
        ConfigurationHeaderInput.Text = configuration?.ConfigurationHeader ?? "";
        EntryPointInput.Text = configuration?.EntryPoint ?? "";
        IncludeDirectoriesInput.Text = Lines(configuration?.IncludeDirectories);
        ExclusionsInput.Text = Lines(configuration?.SourceExclusions);
        ResourceDirectoriesInput.Text = Lines(configuration?.ResourceDirectories);
        AdditionalSourcesInput.Clear();
        WidthInput.Text = (configuration?.Width ?? 240).ToString(CultureInfo.InvariantCulture);
        HeightInput.Text = (configuration?.Height ?? 320).ToString(CultureInfo.InvariantCulture);
        ColorDepthPicker.SelectedItem = configuration?.ColorDepth ?? 16;
        LibraryCandidates.ItemsSource = null;
        ConfigurationHeaders.ItemsSource = null;
        EntryPoints.ItemsSource = null;
        SourceCandidates.ItemsSource = null;
        sources.Clear();
        Diagnostics.ItemsSource = null;
        DiscoverySummary.Text = "扫描结果将显示真正的库根目录和精确版本。";
        SelectedLibraryLabel.Text = "";
        ReviewSummary.Text = "";
        Notice.Text = directory is null ? "先打开工程，再配置自定义 UI。" : "选择现有 LVGL 库目录后扫描。";
        initializing = false;
        ShowStep(0);
        UpdateButtons();
    }

    public async Task InitializeExistingAsync()
    {
        if (existing is null || service is null || project is null || busy || shutdown)
        {
            return;
        }
        operation = RunAsync(async token =>
        {
            await DiscoverAsync(token);
            if (CanUseLibrary(SelectedLibrary))
            {
                await InspectAsync(token);
            }
        });
        await operation;
    }

    private LvglLibraryCandidate? SelectedLibrary => (LibraryCandidates.SelectedItem as LibraryChoice)?.Candidate;
    private static bool CanUseLibrary(LvglLibraryCandidate? candidate) => candidate is { IsSupported: true, IsComplete: true }
        && !Path.IsPathRooted(candidate.Directory);
    private void LibraryStep_Click(object sender, RoutedEventArgs e) => ShowStep(0);
    private void UiStep_Click(object sender, RoutedEventArgs e)
    {
        if (CanUseLibrary(SelectedLibrary))
        {
            ShowStep(1);
        }
    }
    private void ReviewStep_Click(object sender, RoutedEventArgs e)
    {
        if (inspection is not null)
        {
            ReviewSummary.Text = $"LVGL {SelectedLibrary?.Version ?? "—"} · 入口 {EntryPointInput.Text.Trim()} · 请复核勾选文件";
            ShowStep(2);
        }
    }
    private void ShowStep(int value)
    {
        LibraryStep.Visibility = value == 0 ? Visibility.Visible : Visibility.Collapsed;
        UiStep.Visibility = value == 1 ? Visibility.Visible : Visibility.Collapsed;
        ReviewStep.Visibility = value == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void LibraryBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择 LVGL 库或其外层目录");
        if (path is not null)
        {
            LibrarySearchInput.Text = path;
            LibraryCandidates.ItemsSource = null;
            InvalidateInspection();
        }
    }
    private void UiBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择共享 UI 源码目录");
        if (path is not null)
        {
            UiDirectoryInput.Text = path;
        }
    }
    private string? PickFolder(string title)
    {
        if (busy || shutdown || project is null)
        {
            return null;
        }
        var picker = new OpenFolderDialog { Title = title, InitialDirectory = project };
        return picker.ShowDialog(Window.GetWindow(this)) == true ? ProjectPath(picker.FolderName) : null;
    }
    private void HeaderBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (busy || shutdown || project is null)
        {
            return;
        }
        var picker = new OpenFileDialog { Title = "选择与 MCU 共用的 lv_conf.h", Filter = "LVGL 配置 (lv_conf.h)|lv_conf.h|头文件 (*.h)|*.h", CheckFileExists = true, InitialDirectory = project };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            ConfigurationHeaderInput.Text = ProjectPath(picker.FileName);
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        operation = RunAsync(DiscoverAsync);
        await operation;
    }
    private async Task DiscoverAsync(CancellationToken token)
    {
        if (service is null || project is null)
        {
            return;
        }
        Notice.Text = "正在扫描 LVGL 库目录…";
        var report = await service.DiscoverAsync(project, [ProjectPath(LibrarySearchInput.Text)], token);
        token.ThrowIfCancellationRequested();
        initializing = true;
        var choices = report.Candidates.Select(candidate => new LibraryChoice(candidate)).ToArray();
        LibraryCandidates.ItemsSource = choices;
        var previous = existing?.LvglDirectory;
        LibraryCandidates.SelectedItem = choices.FirstOrDefault(choice => choice.Directory.Equals(previous, StringComparison.OrdinalIgnoreCase))
            ?? (choices.Length == 1 ? choices[0] : null);
        initializing = false;
        InvalidateInspection();
        ShowDiagnostics(report.Diagnostics.Concat(SelectedLibrary?.Diagnostics ?? []));
        DiscoverySummary.Text = $"发现 {choices.Length} 个候选库；请按目录与精确版本选择。";
        SelectedLibraryLabel.Text = SelectedLibrary is { } selected ? $"LVGL {selected.Version ?? "版本未识别"} · {selected.Directory}" : "";
        Notice.Text = choices.Length == 0 ? "没有发现完整 LVGL 库，请根据诊断选择真正的库或其外层目录。" :
            CanUseLibrary(SelectedLibrary) ? "库已识别，下一步选择 UI 与共享配置。" : "请选择可用于预览的完整库；跨盘库需要先导入工程同盘。";
    }

    private void LibraryCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }
        InvalidateInspection();
        var candidate = SelectedLibrary;
        SelectedLibraryLabel.Text = candidate is null ? "" : $"LVGL {candidate.Version ?? "版本未识别"} · {candidate.Directory}";
        ShowDiagnostics(candidate?.Diagnostics ?? []);
        UpdateButtons();
    }
    private void UiDirectoryEdited(object sender, TextChangedEventArgs e)
    {
        if (!initializing)
        {
            InvalidateInspection();
        }
    }
    private void LibrarySearchEdited(object sender, TextChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }
        LibraryCandidates.ItemsSource = null;
        SelectedLibraryLabel.Text = "";
        InvalidateInspection();
        DiscoverySummary.Text = "扫描目录已修改，请重新扫描并选择库。";
    }
    private void InvalidateInspection()
    {
        inspection = null;
        validated = false;
        sources.Clear();
        SourceCandidates.ItemsSource = null;
        ConfigurationHeaders.ItemsSource = null;
        EntryPoints.ItemsSource = null;
        UpdateButtons();
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        operation = RunAsync(InspectAsync);
        await operation;
    }
    private async Task InspectAsync(CancellationToken token)
    {
        if (service is null || project is null || SelectedLibrary is not { } library || !CanUseLibrary(library))
        {
            return;
        }
        Notice.Text = "正在分析共享 UI、源码、配置与入口…";
        var result = await service.InspectUiAsync(project, library.Directory, ProjectPath(UiDirectoryInput.Text), token);
        token.ThrowIfCancellationRequested();
        initializing = true;
        inspection = result;
        validated = false;
        ConfigurationHeaders.ItemsSource = result.ConfigurationHeaders;
        ConfigurationHeaders.SelectedItem = result.ConfigurationHeaders.FirstOrDefault(path => path.Equals(ConfigurationHeaderInput.Text, StringComparison.OrdinalIgnoreCase))
            ?? (result.ConfigurationHeaders.Count == 1 ? result.ConfigurationHeaders[0] : null);
        if (ConfigurationHeaderInput.Text.Length == 0 && ConfigurationHeaders.SelectedItem is string header)
        {
            ConfigurationHeaderInput.Text = header;
        }
        var entries = result.EntryPoints.Select(entry => new EntryChoice(entry)).ToArray();
        EntryPoints.ItemsSource = entries;
        EntryPoints.SelectedItem = entries.FirstOrDefault(choice => choice.Entry.Name.Equals(EntryPointInput.Text, StringComparison.Ordinal))
            ?? (entries.Length == 1 ? entries[0] : null);
        if (EntryPointInput.Text.Length == 0 && EntryPoints.SelectedItem is EntryChoice entry)
        {
            EntryPointInput.Text = entry.Entry.Name;
        }
        var currentSources = existing?.SourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        sources.Clear();
        foreach (var path in result.SourceFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(new(path, currentSources?.Contains(path) ?? true, "扫描推荐的共享 UI / 字体 / 图片源码"));
        }
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Code is "UI_HARDWARE_SOURCE" or "UI_MAIN_SOURCE" && d.Path is not null))
        {
            if (sources.All(choice => !choice.Path.Equals(diagnostic.Path, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new(diagnostic.Path!, currentSources?.Contains(diagnostic.Path!) == true, "默认排除 · " + diagnostic.Message));
            }
        }
        if (existing is not null)
        {
            foreach (var path in existing.SourceFiles.Where(path => sources.All(choice => !choice.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            {
                sources.Add(new(path, true, "当前配置已有的源码；保留供审阅"));
            }
        }
        SourceCandidates.ItemsSource = sources.ToArray();
        if (existing is null)
        {
            IncludeDirectoriesInput.Text = Lines(result.IncludeDirectories);
            ResourceDirectoriesInput.Text = Lines(result.ResourceDirectories);
        }
        initializing = false;
        ShowDiagnostics(result.Diagnostics);
        Notice.Text = $"已分析 {sources.Count} 个源码候选、{result.EntryPoints.Count} 个入口。入口名称可手动修改；下一步审阅编译内容。";
        UpdateButtons();
    }

    private void ConfigurationHeaders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && ConfigurationHeaders.SelectedItem is string header)
        {
            ConfigurationHeaderInput.Text = header;
        }
    }
    private void EntryPoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && EntryPoints.SelectedItem is EntryChoice entry)
        {
            EntryPointInput.Text = entry.Entry.Name;
        }
    }
    private void SetupEdited(object sender, RoutedEventArgs e)
    {
        if (initializing)
        {
            return;
        }
        validated = false;
        UpdateButtons();
    }

    private LvglPreviewConfiguration ReadSetup()
    {
        if (project is null || SelectedLibrary is not { } library || !CanUseLibrary(library) || inspection is null)
        {
            throw new StudioXException("LVGL_SETUP_REQUIRED", "请先选择完整 LVGL 库并分析 UI 源码。");
        }
        if (!int.TryParse(WidthInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            throw new StudioXException("LVGL_DISPLAY_SETTINGS", "显示宽度和高度需要填写正整数。");
        }
        var chosenSources = sources.Where(choice => choice.Selected).Select(choice => choice.Path)
            .Concat(Paths(AdditionalSourcesInput.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var baseline = existing ?? new LvglPreviewConfiguration(1, library.Directory, "", [], [], "");
        return baseline with
        {
            LvglDirectory = library.Directory,
            ConfigurationHeader = ProjectPath(ConfigurationHeaderInput.Text),
            UiDirectory = ProjectPath(UiDirectoryInput.Text),
            EntryPoint = EntryPointInput.Text.Trim(),
            SourceFiles = chosenSources,
            IncludeDirectories = Paths(IncludeDirectoriesInput.Text),
            SourceExclusions = Paths(ExclusionsInput.Text),
            ResourceDirectories = Paths(ResourceDirectoriesInput.Text),
            Width = width,
            Height = height,
            ColorDepth = ColorDepthPicker.SelectedItem is int depth ? depth : baseline.ColorDepth
        };
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        operation = RunAsync(ValidateAsync);
        await operation;
    }
    private async Task ValidateAsync(CancellationToken token)
    {
        if (service is null || project is null)
        {
            return;
        }
        Notice.Text = "正在检查库、源码、入口与配置依赖…";
        var result = await service.ValidateSetupAsync(project, ReadSetup(), token);
        token.ThrowIfCancellationRequested();
        validated = result.IsValid;
        ShowDiagnostics(result.Diagnostics);
        Notice.Text = result.IsValid ? "配置检查通过，可保存或直接启动；实际编译诊断仍会完整保留。" : "配置存在错误，请按诊断修改后重新检查。";
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveStart_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private async Task SaveAsync(bool start)
    {
        operation = RunAsync(async token =>
        {
            if (service is null || project is null)
            {
                return;
            }
            var config = ReadSetup();
            await ValidateAsync(token);
            if (!validated)
            {
                return;
            }
            Notice.Text = "正在保存工程相对路径的预览配置…";
            await service.SaveConfigurationAsync(project, config, token);
            existing = config;
            if (SavedAsync is not null)
            {
                await SavedAsync(config, start, token);
            }
            Notice.Text = start ? "配置已保存，预览启动结果见运行状态与日志。" : "配置已保存，可启动预览。";
        });
        await operation;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy || shutdown || project is null)
        {
            return;
        }
        var currentRevision = revision;
        using var source = new CancellationTokenSource();
        cancellation = source;
        busy = true;
        UpdateButtons();
        BusyChanged?.Invoke(true);
        try
        {
            await action(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (currentRevision == revision && !shutdown)
            {
                Notice.Text = "已取消当前操作，已有配置保持不变。";
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic?.Invoke("LVGL UI 配置：" + ex);
            if (currentRevision == revision && !shutdown)
            {
                Notice.Text = ex.Message;
                ShowDiagnostics([new("LVGL_SETUP_EXCEPTION", "error", ex.ToString())]);
            }
        }
        finally
        {
            if (ReferenceEquals(cancellation, source))
            {
                cancellation = null;
            }
            busy = false;
            BusyChanged?.Invoke(false);
            if (!shutdown)
            {
                UpdateButtons();
            }
        }
    }

    private void ShowDiagnostics(IEnumerable<LvglDiagnostic> diagnostics) => Diagnostics.ItemsSource =
        diagnostics.Distinct().Select(diagnostic => new DiagnosticChoice(diagnostic)).ToArray();
    private void UpdateButtons()
    {
        if (initializing)
        {
            return;
        }
        var available = !shutdown && !busy && project is not null;
        var hasLibrary = CanUseLibrary(SelectedLibrary);
        LibrarySearchInput.IsEnabled = LibraryBrowseButton.IsEnabled = DiscoverButton.IsEnabled = LibraryCandidates.IsEnabled = available;
        LibraryStepButton.IsEnabled = available;
        UiStepButton.IsEnabled = LibraryNextButton.IsEnabled = available && hasLibrary;
        ReviewStepButton.IsEnabled = UiNextButton.IsEnabled = available && inspection is not null;
        UiDirectoryInput.IsEnabled = UiBrowseButton.IsEnabled = InspectButton.IsEnabled = available && hasLibrary;
        ConfigurationHeaderInput.IsEnabled = HeaderBrowseButton.IsEnabled = ConfigurationHeaders.IsEnabled = available && hasLibrary;
        EntryPointInput.IsEnabled = EntryPoints.IsEnabled = available && hasLibrary;
        SourceCandidates.IsEnabled = AdditionalSourcesInput.IsEnabled = IncludeDirectoriesInput.IsEnabled = ExclusionsInput.IsEnabled = ResourceDirectoriesInput.IsEnabled = DisplayInputs.IsEnabled = available;
        ValidateButton.IsEnabled = available && inspection is not null;
        SaveButton.IsEnabled = SaveStartButton.IsEnabled = available && validated;
        CloseButton.IsEnabled = !shutdown && !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        await CancelAsync();
        Closed?.Invoke();
    }
    public async Task CancelAsync()
    {
        cancellation?.Cancel();
        await operation;
    }
    public async Task ShutdownAsync()
    {
        shutdown = true;
        await CancelAsync();
    }

    private string ProjectPath(string value)
    {
        var path = value.Trim().Trim('"');
        if (path.Length == 0)
        {
            return "";
        }
        if (project is null)
        {
            throw new StudioXException("PROJECT_REQUIRED", "请先打开工程。");
        }
        return Path.GetRelativePath(project, Path.GetFullPath(path, project)).Replace('\\', '/');
    }
    private string[] Paths(string input) => input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(ProjectPath).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string Lines(IEnumerable<string>? paths) => paths is null ? "" : string.Join(Environment.NewLine, paths);

    /// <summary>离线界面验证入口：调用真实扫描服务，显示审阅页，不保存配置或启动进程。</summary>
    internal async Task InspectForPreviewAsync(string librarySearchDirectory, string uiDirectory)
    {
        LibrarySearchInput.Text = ProjectPath(librarySearchDirectory);
        UiDirectoryInput.Text = ProjectPath(uiDirectory);
        operation = RunAsync(async token =>
        {
            await DiscoverAsync(token);
            if (!CanUseLibrary(SelectedLibrary))
            {
                throw new StudioXException("LVGL_PREVIEW_CANDIDATE", "离线布局验证需要唯一或已配置的受支持库。");
            }
            await InspectAsync(token);
        });
        await operation;
        ReviewStep_Click(this, new RoutedEventArgs());
    }

    internal void ShowStepForPreview(int value) => ShowStep(value);
}
