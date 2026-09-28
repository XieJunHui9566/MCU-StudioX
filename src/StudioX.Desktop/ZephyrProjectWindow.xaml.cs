namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Packages;

/// <summary>独立展示 Zephyr 实验包与板级目标，避免把芯片型号当作板型。</summary>
public partial class ZephyrProjectWindow : Window
{
    private readonly ZephyrPackRepository repository;

    public ZephyrProjectWindow(ZephyrPackRepository repository)
    {
        this.repository = repository;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public InstalledZephyrPack SelectedPack => (InstalledZephyrPack)PackPicker.SelectedItem;
    public ZephyrBoardDefinition SelectedBoard => (ZephyrBoardDefinition)BoardPicker.SelectedItem;
    public ZephyrProjectTemplate SelectedTemplate => (ZephyrProjectTemplate)TemplatePicker.SelectedItem;
    public string ProjectName => ProjectNameInput.Text.Trim();

    private async Task<ZephyrPackCatalogReport?> RefreshAsync(string? selectedId = null, string? selectedVersion = null)
    {
        try
        {
            var report = await repository.ListCatalogReportAsync();
            var packs = report.Packs;
            PackPicker.ItemsSource = packs;
            PackPicker.SelectedItem = packs.FirstOrDefault(pack => pack.Manifest.Id == selectedId &&
                pack.Manifest.Version == selectedVersion);
            StatusText.Text = packs.Count == 0
                ? "尚未安装 Zephyr 实验包。请导入专用 .mcupack。"
                : $"已安装 {packs.Count} 个 Zephyr 实验包。请选择准确的板级目标。";
            if (report.Failures.Count > 0)
            {
                StatusText.Text += $" 另有 {report.Failures.Count} 个受损包条目，悬停查看诊断。";
                StatusText.ToolTip = string.Join("\n\n", report.Failures.Select(failure =>
                    failure.Directory + "\n" + failure.Diagnostic));
            }
            else { StatusText.ToolTip = null; }
            return report;
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取 Zephyr 包失败：" + ex.Message;
            StatusText.ToolTip = ex.ToString();
            return null;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Zephyr 实验包|*.mcupack" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var pack = await repository.ImportAsync(dialog.FileName);
            var report = await RefreshAsync(pack.Manifest.Id, pack.Manifest.Version);
            StatusText.Text = $"已导入 Zephyr 实验包：{pack.Manifest.DisplayName} {pack.Manifest.Version}。" +
                (report is { Failures.Count: > 0 } ? $" 另有 {report.Failures.Count} 个受损包条目，悬停查看诊断。" : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void Pack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoardPicker is null || TemplatePicker is null)
        {
            return;
        }
        BoardPicker.ItemsSource = (PackPicker.SelectedItem as InstalledZephyrPack)?.Manifest.Boards;
        BoardPicker.SelectedIndex = -1;
        BoardPicker.IsEnabled = BoardPicker.Items.Count > 0;
        TemplatePicker.ItemsSource = null;
        TemplatePicker.IsEnabled = false;
        BoardNote.Text = "";
        UpdateCreateState();
    }

    private void Board_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatePicker is null)
        {
            return;
        }
        var board = BoardPicker.SelectedItem as ZephyrBoardDefinition;
        TemplatePicker.ItemsSource = board?.Templates;
        TemplatePicker.SelectedIndex = -1;
        TemplatePicker.IsEnabled = TemplatePicker.Items.Count > 0;
        BoardNote.Text = board is null ? "" : $"目标：{board.BoardTarget} · 板修订：{board.BoardRevision}\n{board.DocumentationNote}";
        UpdateCreateState();
    }

    private void Template_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCreateState();

    private void UpdateCreateState()
    {
        if (CreateButton is not null)
        {
            CreateButton.IsEnabled = PackPicker?.SelectedItem is InstalledZephyrPack &&
                BoardPicker?.SelectedItem is ZephyrBoardDefinition &&
                TemplatePicker?.SelectedItem is ZephyrProjectTemplate;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PackValidator.Token(ProjectName);
            if (!SelectedPack.Manifest.Experimental)
            {
                throw new InvalidOperationException("当前入口只接受明确标记为实验模式的 Zephyr 包。");
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ProjectNameInput.Focus();
        }
    }
}
