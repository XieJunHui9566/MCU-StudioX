namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application;
using StudioX.Engine.Hdl;

/// <summary>管理电路图导航和选择；综合、输入校验及导出由应用服务处理。</summary>
public partial class HdlSchematicView : UserControl
{
    private HdlSchematicService? service;
    private readonly Stack<string> history = new();
    private bool changingModule;
    private double zoom = 1;
    private bool stale;
    public HdlSchematicResult? Result
    {
        get; private set;
    }
    public event EventHandler? GenerateRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? LogRequested;
    public event Action<string>? SourceRequested;

    public HdlSchematicView()
    {
        InitializeComponent();
        Surface.SelectionChanged += SelectionChanged;
        Surface.NodeActivated += ActivateNode;
        foreach (var box in new[] { Sources, Includes, Defines, TopModule })
        {
            box.TextChanged += (_, _) => { if (Result is not null) { MarkStale(); } };
        }
        Flatten.Click += (_, _) => { if (Result is not null) { MarkStale(); } };
    }

    public void Configure(HdlSchematicService value, HdlSchematicSettings settings)
    {
        service = value;
        Sources.Text = string.Join("\n", settings.Sources);
        Includes.Text = string.Join("\n", settings.IncludeDirectories);
        Defines.Text = string.Join(" ", settings.Defines);
        TopModule.Text = settings.TopModule;
        Flatten.IsChecked = settings.Flatten;
    }

    public HdlSchematicSettings ReadSettings() => new(1, Lines(Sources.Text), Lines(Includes.Text),
        Defines.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), TopModule.Text.Trim(), Flatten.IsChecked == true);

    public void Clear(string message = "点击“生成电路图”查看当前 Verilog 的逻辑结构。")
    {
        Result = null;
        stale = false;
        history.Clear();
        Modules.ItemsSource = null;
        Surface.SetDiagram(null);
        Summary.Text = message;
        ExportButton.IsEnabled = LogButton.IsEnabled = BackButton.IsEnabled = false;
    }

    public void SetBusy(bool busy)
    {
        GenerateButton.IsEnabled = SettingsPanel.IsEnabled = !busy;
    }

    public void ShowResult(HdlSchematicResult result)
    {
        Result = result;
        stale = false;
        history.Clear();
        changingModule = true;
        Modules.ItemsSource = result.Modules.Select(module => module.Name).ToArray();
        changingModule = false;
        Modules.SelectedItem = result.TopModule;
        ExportButton.IsEnabled = LogButton.IsEnabled = true;
    }

    public void MarkStale()
    {
        stale = true;
        Summary.Text = "源码或配置已改变，下图属于上次综合快照；请重新生成。";
    }

    private void ShowModule(string name)
    {
        if (Result?.Modules.FirstOrDefault(item => item.Name == name) is not { } module || service is null)
        {
            return;
        }
        Surface.SetDiagram(service.CreateDiagram(module));
        Summary.Text = $"{name} · {module.Cells.Length} 个元件 · {module.Ports.Length} 个端口 · RTL 综合结果（未做布局布线或时序分析）";
        if (module.IsBlackBox)
        {
            Summary.Text += " · 黑盒模块，仅有端口声明";
        }
        if (Result.Warnings.Length > 0)
        {
            Summary.Text += $" · {Result.Warnings.Length} 条警告，请查看综合日志";
        }
        if (stale)
        {
            MarkStale();
        }
        Details.Text = "双击子模块进入层级。\n点击元件或连线查看端口、位宽与连接。\nCtrl + 滚轮缩放，滚动条移动视图。";
        BackButton.IsEnabled = history.Count > 0;
        Dispatcher.InvokeAsync(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    internal void ActivateNode(HdlDiagramNode node)
    {
        if (Result?.Modules.Any(module => module.Name == node.Type) == true)
        {
            history.Push((string)Modules.SelectedItem);
            Modules.SelectedItem = node.Type;
        }
        else if (node.Source is { } source)
        {
            SourceRequested?.Invoke(source);
        }
    }

    private void SelectionChanged(object? value)
    {
        SourceButton.IsEnabled = value is HdlDiagramNode { Source: not null };
        Details.Text = value switch
        {
            HdlDiagramNode node => Describe(node),
            HdlDiagramWire wire => wire.Label + "\n\n" + wire.SourceNode + " → " + wire.TargetNode + "\n\n" + string.Join("\n", wire.BitMappings),
            _ => "选择元件或信号线。",
        };
    }

    private string Describe(HdlDiagramNode node)
    {
        var cell = Result?.Modules.FirstOrDefault(item => item.Name == (string?)Modules.SelectedItem)?.Cells
            .FirstOrDefault(item => "cell:" + item.Name == node.Id);
        return node.Label + "\n类型：" + node.Type + "\n\n" + string.Join("\n", node.Inputs.Concat(node.Outputs)
            .Select(pin => $"{pin.Direction}  {pin.Name} [{pin.Bits.Length}]"))
            + (cell is null ? "" : "\n\n参数\n" + string.Join("\n", cell.Parameters.Select(pair => pair.Key + " = " + pair.Value)))
            + "\n\n" + node.Source;
    }

    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, 0.08, 3);
        Surface.LayoutTransform = new ScaleTransform(zoom, zoom);
        ZoomText.Text = $"{zoom:P0}";
    }

    private void Fit()
    {
        if (Surface.Diagram is { } diagram && Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
        {
            SetZoom(Math.Min(1, Math.Min((Viewport.ActualWidth - 25) / diagram.Width, (Viewport.ActualHeight - 25) / diagram.Height)));
        }
    }

    private void Viewport_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }
        SetZoom(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private void Generate_Click(object sender, RoutedEventArgs e) => GenerateRequested?.Invoke(this, EventArgs.Empty);
    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void Log_Click(object sender, RoutedEventArgs e) => LogRequested?.Invoke(this, EventArgs.Empty);
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (Surface.Selection is HdlDiagramNode { Source: { } source })
        {
            SourceRequested?.Invoke(source);
        }
    }
    private void Module_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!changingModule && Modules.SelectedItem is string module)
        {
            ShowModule(module);
        }
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (history.TryPop(out var module))
        {
            Modules.SelectedItem = module;
        }
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoom / 1.2);
}
