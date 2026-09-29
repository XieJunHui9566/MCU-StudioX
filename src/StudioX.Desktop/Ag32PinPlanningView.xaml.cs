namespace StudioX.Desktop;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>呈现图形引脚草稿、时钟字段和厂商约束结果；文件校验与保存由应用服务负责。</summary>
public partial class Ag32PinPlanningView : UserControl
{
    private readonly ObservableCollection<Ag32PinAssignment> assignments = [];
    private bool loading;
    private bool busy;
    private Ag32PinPlanConflict[] conflicts = [];
    private int? selectedPin;
    private int pinMenuGeneration;
    internal Ag32PinFunctionMenu? ActivePinMenu
    {
        get; private set;
    }
    private int diagramPinCount;
    private string? vexPath;
    private string? sdcPath;

    public Ag32PinPlanningView()
    {
        InitializeComponent();
        AssignmentsGrid.ItemsSource = assignments;
        PackageDiagram.PinSelected += Diagram_PinSelected;
        Unloaded += (_, _) => ClosePinMenu();
    }

    public event EventHandler? SaveRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler<string>? OpenConstraintRequested;

    public Ag32PinPlanSnapshot? Snapshot
    {
        get; private set;
    }
    public bool HasChanges
    {
        get; private set;
    }

    public void SetSnapshot(Ag32PinPlanSnapshot snapshot)
    {
        ClosePinMenu();
        loading = true;
        try
        {
            Snapshot = snapshot;
            Visibility = Visibility.Visible;
            assignments.Clear();
            foreach (var assignment in snapshot.Assignments.OrderBy(item => item.PinNumber))
            {
                assignments.Add(assignment);
            }
            selectedPin = null;
            HseClock.Text = ClockText(snapshot.Clocks.HseMhz);
            SysClock.Text = ClockText(snapshot.Clocks.SysMhz);
            BusClock.Text = ClockText(snapshot.Clocks.BusMhz);
            var package = Ag32DeviceCatalog.Require(snapshot.DeviceId).PackageName;
            PackageText.Text = $"{snapshot.DeviceId} · {snapshot.TargetDevice} · {package} · 厂商可映射脚 {snapshot.Pins.Count(pin => pin.CanAssign)} 个";
            PackageDiagram.SetPackage(snapshot.DeviceId, package);
            HasChanges = false;
            vexPath = sdcPath = null;
            ConstraintDetails.Text = "保存后显示实际生成的约束文件；这些预览文件不代表已编译或已烧录。";
            VexButton.IsEnabled = SdcButton.IsEnabled = false;
            PlannerStatus.Text = snapshot.Diagnostics.Length == 0
                ? "左键点击封装引脚，在菜单中选择功能或清除分配；保存前统一检查引脚、复用资源和时钟。"
                : string.Join("\n", snapshot.Diagnostics);
            UpdateDiagram();
            if (diagramPinCount != snapshot.Pins.Length)
            {
                // 高脚数封装保留号码与引脚之间的可读间距，通过滚动查看，不压缩成紧密小图。
                var initialZoom = Math.Round(PackageDiagram.Width * 0.9);
                // 常用小封装初始完整显示四边与编号；高脚数保持可读字号并允许滚动。
                DiagramZoom.Value = Math.Clamp(snapshot.Pins.Length <= 48 ? Math.Min(initialZoom, 560) : initialZoom,
                    480, DiagramZoom.Maximum);
                diagramPinCount = snapshot.Pins.Length;
            }
            SetBusy(busy);
        }
        finally
        {
            loading = false;
        }
    }

    public void SetBusy(bool value)
    {
        busy = value;
        if (value)
        {
            ClosePinMenu();
        }
        var editable = !value && Snapshot?.CanEdit == true;
        HseClock.IsEnabled = SysClock.IsEnabled = BusClock.IsEnabled = editable;
        PinName.IsEnabled = editable && assignments.Any(item => item.PinNumber == selectedPin);
        PinDirection.IsEnabled = PinName.IsEnabled && assignments.Any(item => item.PinNumber == selectedPin && item.Function.StartsWith("GPIO", StringComparison.Ordinal));
        PinPull.IsEnabled = PinDirection.IsEnabled;
        PinOutputType.IsEnabled = PinDirection.IsEnabled && assignments.FirstOrDefault(item => item.PinNumber == selectedPin)?.Direction != "INPUT";
        PackageDiagram.IsEnabled = !value;
        SavePlanButton.IsEnabled = editable && conflicts.Length == 0;
        ReloadPlanButton.IsEnabled = !value;
    }

    public void Clear()
    {
        ClosePinMenu();
        Snapshot = null;
        selectedPin = null;
        HasChanges = false;
        diagramPinCount = 0;
        assignments.Clear();
        conflicts = [];
        ConflictStatus.Text = "";
        ConflictStatus.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    public Ag32PinAssignment[] GetAssignments() => assignments.ToArray();

    public Ag32PinClockSettings GetClocks() => new(
        ParseClock(HseClock.Text, "HSECLK"),
        ParseClock(SysClock.Text, "SYSCLK"),
        ParseClock(BusClock.Text, "BUSCLK"));

    public void ShowResult(Ag32PinPlanResult result)
    {
        SetSnapshot(result.Snapshot);
        vexPath = result.VexPath;
        sdcPath = result.SdcPath;
        ConstraintDetails.Text = $"VE：{result.Snapshot.SourcePath}\n系统头文件：{Ag32SystemSupport.HeaderPath}\n系统实现：{Ag32SystemSupport.SourcePath}\n引脚约束数据：{result.VexPath}\n时钟约束：{result.SdcPath}\n\n最终 ASF 和映射镜像由顶部编译生成。";
        VexButton.IsEnabled = SdcButton.IsEnabled = true;
        PlannerStatus.Text = "已保存名称注释、系统代码和约束；主函数引用 StudioX_System.h 即可使用，顶部编译生成映射镜像。";
    }

    public void ShowFailure(string message) => PlannerStatus.Text = message;

    private void Diagram_PinSelected(object? sender, int number)
    {
        if (Snapshot is null || busy)
        {
            return;
        }
        ClosePinMenu();
        selectedPin = number;
        AssignmentsGrid.SelectedItem = assignments.FirstOrDefault(item => item.PinNumber == number);
        UpdatePinDetails();
        var snapshot = Snapshot;
        var pin = snapshot.Pins.Single(pin => pin.Number == number);
        var anchor = PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, number));
        var menuGeneration = pinMenuGeneration;
        var menu = new Ag32PinFunctionMenu(anchor, pin, snapshot.Functions, assignments, snapshot.CanEdit,
            function =>
            {
                if (menuGeneration == pinMenuGeneration)
                {
                    AssignPin(snapshot, pin, function);
                }
            });
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(ActivePinMenu, menu))
            {
                ActivePinMenu = null;
            }
        };
        ActivePinMenu = menu;
        menu.IsOpen = true;
    }

    private void ClosePinMenu()
    {
        pinMenuGeneration++;
        if (ActivePinMenu is { } menu)
        {
            menu.Dismiss();
            ActivePinMenu = null;
        }
    }

    private void AssignPin(Ag32PinPlanSnapshot snapshot, Ag32PackagePin pin, Ag32PinFunction? function)
    {
        // 菜单可能在工程切换、后台刷新或构建前打开；旧菜单不能再修改新草稿。
        if (!ReferenceEquals(Snapshot, snapshot) || !snapshot.CanEdit || !pin.CanAssign || busy)
        {
            return;
        }
        ClosePinMenu();
        var previous = assignments.Where(item => item.PinNumber == pin.Number).ToArray();
        if (function is null && previous.Length == 0 ||
            function is not null && previous.Length == 1 && previous[0].Function == function.Name)
        {
            return;
        }
        // 替换仅作用于点中的物理脚；其它脚的相同功能保留并由冲突检查标红。
        foreach (var assignment in previous)
        {
            assignments.Remove(assignment);
        }
        if (function is not null)
        {
            var added = new Ag32PinAssignment(function.Name, pin.Number, Name: previous.FirstOrDefault()?.Name);
            assignments.Add(added);
            AssignmentsGrid.SelectedItem = added;
        }
        selectedPin = pin.Number;
        MarkChanged();
        UpdateDiagram();
    }

    private void UpdateDiagram()
    {
        if (Snapshot is null)
        {
            return;
        }
        conflicts = Ag32PinPlanConflicts.Find(assignments, Snapshot.Functions);
        ConflictStatus.Text = conflicts.Length == 0 ? "" : (Snapshot.CanEdit
            ? "引脚分配冲突，移除重复分配后才能保存：\n"
            : "VE 包含分配冲突，请先在文本编辑器修正并重新读取：\n")
            + string.Join("\n", conflicts.Select(conflict => conflict.Message));
        ConflictStatus.Visibility = conflicts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PackageDiagram.SetPins(Snapshot.Pins.Select(pin => new Ag32PackagePinVisual(pin.Number, pin.CanAssign,
            string.Join(", ", assignments.Where(item => item.PinNumber == pin.Number).Select(item => item.Name is { } name ? name + " · " + Ag32PinFunctionLabels.Compact(item.Function) : Ag32PinFunctionLabels.Compact(item.Function))),
            ConflictForPin(pin.Number))).ToArray(),
            selectedPin);
        SetBusy(busy);
        UpdatePinDetails();
    }

    private string? ConflictForPin(int number)
    {
        var messages = conflicts.Where(conflict => conflict.PinNumbers.Contains(number)).Select(conflict => conflict.Message).ToArray();
        return messages.Length == 0 ? null : string.Join("\n", messages);
    }

    private void MarkChanged()
    {
        if (!loading)
        {
            HasChanges = true;
            PlannerStatus.Text = "图形配置尚未保存。保存并生成约束后，顶部编译才会使用这些更改。";
            VexButton.IsEnabled = SdcButton.IsEnabled = false;
        }
    }

    private void UpdatePinDetails()
    {
        if (PinName is null) { return; }
        var wasLoading = loading;
        loading = true;
        var selected = assignments.FirstOrDefault(item => item.PinNumber == selectedPin);
        PinName.Text = selected?.Name ?? "";
        PinDirection.SelectedValue = selected?.Direction is "INPUT" or "OUTPUT" ? selected.Direction : "";
        PinPull.SelectedValue = selected?.Pull ?? "NONE";
        PinOutputType.SelectedValue = selected?.OutputType ?? "PUSH_PULL";
        PinName.IsEnabled = !busy && Snapshot?.CanEdit == true && selected is not null;
        PinDirection.IsEnabled = PinName.IsEnabled && selected!.Function.StartsWith("GPIO", StringComparison.Ordinal);
        PinPull.IsEnabled = PinDirection.IsEnabled;
        PinOutputType.IsEnabled = PinDirection.IsEnabled && selected?.Direction != "INPUT";
        loading = wasLoading;
        var pin = Snapshot?.Pins.FirstOrDefault(pin => pin.Number == selectedPin);
        var functions = string.Join(", ", assignments.Where(item => item.PinNumber == pin?.Number).Select(item => Ag32PinFunctionLabels.Display(item.Function)));
        PinDetails.Text = pin is null ? "左键点击左侧引脚，选择功能。"
            : !pin.CanAssign ? $"PIN_{pin.Number} · 固定 / 不可映射"
            : $"PIN_{pin.Number} · " + (functions.Length == 0 ? "空闲" : functions);
        PinDetails.ToolTip = selected is null ? null : Ag32PinFunctionLabels.Description(selected.Function);
    }

    private void Assignment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || Snapshot is null || AssignmentsGrid.SelectedItem is not Ag32PinAssignment assignment)
        {
            return;
        }
        selectedPin = assignment.PinNumber;
        PackageDiagram.SetSelection(selectedPin);
        UpdatePinDetails();
    }

    private void Clock_Changed(object sender, TextChangedEventArgs e) => MarkChanged();
    private void PinName_Changed(object sender, TextChangedEventArgs e) => ChangePinDetails();
    private void PinDirection_Changed(object sender, SelectionChangedEventArgs e) => ChangePinDetails();
    private void ChangePinDetails()
    {
        if (loading || busy || Snapshot?.CanEdit != true || assignments.FirstOrDefault(item => item.PinNumber == selectedPin) is not { } old) { return; }
        var name = PinName.Text.Trim();
        var direction = PinDirection.SelectedValue as string;
        var gpio = old.Function.StartsWith("GPIO", StringComparison.Ordinal);
        var updated = old with { Name = name.Length == 0 ? null : name,
            Direction = gpio ? string.IsNullOrEmpty(direction) ? null : direction : old.Direction,
            Pull = gpio ? PinPull.SelectedValue as string ?? "NONE" : old.Pull,
            OutputType = gpio ? direction == "INPUT" ? "PUSH_PULL" : PinOutputType.SelectedValue as string ?? "PUSH_PULL" : old.OutputType };
        if (updated == old) { return; }
        loading = true;
        assignments[assignments.IndexOf(old)] = updated;
        AssignmentsGrid.SelectedItem = updated;
        loading = false;
        MarkChanged();
        UpdateDiagram();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && Snapshot?.CanEdit == true && conflicts.Length == 0)
        {
            SaveRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);
    private void Vex_Click(object sender, RoutedEventArgs e)
    {
        if (vexPath is not null)
        {
            OpenConstraintRequested?.Invoke(this, vexPath);
        }
    }

    private void Sdc_Click(object sender, RoutedEventArgs e)
    {
        if (sdcPath is not null)
        {
            OpenConstraintRequested?.Invoke(this, sdcPath);
        }
    }

    private static string ClockText(decimal? value) => value?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";

    private static decimal? ParseClock(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new StudioXException("AG32_PIN_PLAN_CLOCK", field + " 请填写正数 MHz，或留空沿用未显式配置状态。");
        }
        return value;
    }
}
