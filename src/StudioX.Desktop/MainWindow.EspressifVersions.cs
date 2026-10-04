namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Tools;
using StudioX.Packages;

public partial class MainWindow
{
    private int idfSelectionRevision;
    private bool applyingIdfSelection;
    private void ClearIdfVersionSelection()
    {
        if (IdfVersionPanel is null || applyingIdfSelection)
        {
            return;
        }
        idfSelectionRevision++;
        IdfVersionPanel.Visibility = Visibility.Collapsed;
        IdfVersionPicker.ItemsSource = null;
    }
    private async Task RefreshIdfVersionsAsync()
    {
        if (IdfVersionPanel is null || applyingIdfSelection)
        {
            return;
        }
        var revision = ++idfSelectionRevision;
        if (PackPicker.SelectedItem is not InstalledPack pack || DevicePicker.SelectedItem is not DeviceDefinition device ||
            TemplatePicker.SelectedItem is not ProjectTemplate template || device.Espressif?.Framework != "esp-idf")
        {
            ClearIdfVersionSelection();
            return;
        }
        IdfVersionPanel.Visibility = Visibility.Visible;
        IdfVersionPicker.IsEnabled = false;
        CreateProjectButton.IsEnabled = false;
        IdfVersionDescription.Text = "正在读取已安装的匹配版本…";
        try
        {
            var choices = await services.EspressifProjectVersions.ListAsync(pack, device.Id, template.Id);
            if (revision != idfSelectionRevision)
            {
                return;
            }
            applyingIdfSelection = true;
            try
            {
                IdfVersionPicker.ItemsSource = choices;
                // 器件包是用户明确选择的输入；初始选择保持该包，不自动换到最新 SDK。
                IdfVersionPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Pack?.Manifest.Version == pack.Manifest.Version);
            }
            finally { applyingIdfSelection = false; }
            UpdateIdfSelectionStatus();
        }
        catch (Exception error)
        {
            if (revision != idfSelectionRevision)
            {
                return;
            }
            IdfVersionPicker.ItemsSource = null;
            IdfVersionDescription.Text = "读取版本失败：" + error.Message;
            CreateProjectButton.IsEnabled = false;
            Log(FailureDiagnostic(error));
        }
        finally { if (revision == idfSelectionRevision) { IdfVersionPicker.IsEnabled = true; } }
    }
    private void IdfVersionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingIdfSelection || IdfVersionDescription is null)
        {
            return;
        }
        if (IdfVersionPicker.SelectedItem is EspressifProjectVersionChoice { Pack: { } pack } choice)
        {
            applyingIdfSelection = true;
            try
            {
                PackPicker.SelectedItem = PackPicker.Items.Cast<InstalledPack>()
                    .Single(item => item.Manifest.Id == pack.Manifest.Id && item.Manifest.Version == pack.Manifest.Version);
                DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single(item => item.Id == choice.DeviceId);
                TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(item => item.Id == choice.TemplateId);
                UpdateSelectedComponents();
            }
            finally { applyingIdfSelection = false; }
        }
        UpdateIdfSelectionStatus();
    }
    private void UpdateIdfSelectionStatus()
    {
        var selected = IdfVersionPicker.SelectedItem as EspressifProjectVersionChoice;
        IdfVersionDescription.Text = selected?.Message ?? "请明确选择 IDF 开发环境组件版本。";
        CreateProjectButton.IsEnabled = selected?.CanCreate == true;
    }
}
