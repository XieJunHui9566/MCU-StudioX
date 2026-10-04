namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

public partial class MainWindow
{
    private PeripheralView? peripheralView;
    private TabItem? peripheralTab;
    private void ShowPeripherals_Click(object sender, RoutedEventArgs e) => _ = RunAsync(async token =>
    {
        if (peripheralTab is null)
        {
            peripheralView = new()
            {
                Requested = RunPeripheralAsync
            };
            peripheralTab = AddToolTab("外设寄存器", peripheralView);
            services.Debugger.Changed += () => _ = Dispatcher.BeginInvoke(() => peripheralView?.ClearReading("调试状态已变化，旧读数已清除。"));
        }
        ShowDocument(peripheralTab);
        peripheralView!.SetDocument(await services.Peripherals.OpenAsync(RequireProject(), token));
    });
    private Task RunPeripheralAsync(string action) => RunAsync(async token =>
    {
        var view = peripheralView!;
        try
        {
            if (action == "import")
            {
                var project = RequireProject();
                var device = currentProjectManifest?.DeviceId ?? throw new InvalidOperationException("工程器件尚未加载。");
                var dialog = new OpenFileDialog { Filter = "CMSIS-SVD|*.svd", Title = "选择与工程器件对应的厂商 SVD" };
                if (dialog.ShowDialog(this) != true)
                {
                    return;
                }
                if (MessageBox.Show(this, $"当前工程器件：{device}\n文件：{dialog.FileName}\n请依据厂商器件包核对映射。确认此 SVD 适用于该器件后再绑定。", "核对 SVD 器件", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                {
                    return;
                }
                view.SetDocument(await services.Peripherals.ImportAsync(project, dialog.FileName, device, token));
            }
            else if (view.Document is { } document && view.Selected is { } register)
            {
                if (action == "read")
                {
                    var accepted = false;
                    if (register.HasReadSideEffects)
                    {
                        accepted = MessageBox.Show(this, $"读取 {register.Path} @0x{register.Address:x8} 可能清除状态或改变外设。确认读取一次？", "读取副作用", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
                        if (!accepted)
                        {
                            return;
                        }
                    }
                    var reading = await services.Peripherals.ReadAsync(document, register, accepted, token);
                    if (ReferenceEquals(view.Document, document) && services.Debugger.PeripheralRevision == reading.Revision)
                    {
                        view.SetReading(reading);
                    }
                }
                else if (action == "write")
                {
                    var preview = services.Peripherals.PreviewWrite(document, register, view.WriteValue);
                    if (MessageBox.Show(this, $"向 {register.Path} @0x{register.Address:x8} 写入完整值 0x{preview.Value:x}（{register.Width} 位）。\n{register.WriteSemantics}\n此操作可改变外设状态，不自动读取或恢复。确认写入？", "确认外设写入", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    await services.Peripherals.WriteAsync(preview, token);
                    view.ClearReading("写入命令已完成，未自动读回。");
                }
            }
            else
            {
                view.ClearReading("请先绑定 SVD 并选择寄存器。");
            }
        }
        catch (Exception ex) { view.ClearReading(ex.ToString()); throw; }
    });
}
