namespace StudioX.Desktop;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
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
        UartDataBits.ItemsSource = new[] { 5, 6, 7, 8 };
        UartStopBits.ItemsSource = new[] { 1, 2 };
        UartPriority.ItemsSource = Enumerable.Range(1, 15).ToArray();
        PlannerTabs.SelectionChanged += PlannerTabs_Changed;
        AssignmentsGrid.ItemsSource = assignments;
        System.Windows.Data.CollectionViewSource.GetDefaultView(assignments).Filter = MatchesPinFilter;
        PackageDiagram.PinSelected += Diagram_PinSelected;
        PackageDiagram.SizeChanged += (_, _) => FitDiagram();
        Unloaded += (_, _) =>
        {
            ClosePinMenu();
            EndDiagramPan();
        };
    }

    public event EventHandler? SaveRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler<string>? OpenConstraintRequested;
    public event EventHandler<Ag32ClockRecommendation>? RecommendedClockRequested;

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
            ReloadPlanButton.ToolTip = snapshot.VerilogSourcePath is null ? "放弃当前草稿，从 .ve 重新读取配置" :
                "放弃当前草稿，从磁盘重新读取 pins.ve 和 Verilog 顶层端口";
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
            LoadAnalog(snapshot);
            LoadVectorPriorities(snapshot);
            LoadUarts(snapshot);
            if (IrqTab.IsSelected)
            {
                RefreshInterruptRows();
            }
            var package = Ag32DeviceCatalog.Require(snapshot.DeviceId).PackageName;
            PackageText.Text = $"{snapshot.DeviceId} · {package}";
            PackageText.ToolTip = $"{snapshot.TargetDevice} · 厂商可映射脚 {snapshot.Pins.Count(pin => pin.CanAssign)} 个";
            PackageDiagram.SetPackage(snapshot.DeviceId, package);
            HasChanges = false;
            timing = null;
            externalDraft = false;
            vexPath = sdcPath = null;
            ConstraintDetails.Text = "保存后显示实际生成的约束文件；这些预览文件不代表已编译或已烧录。";
            VexButton.IsEnabled = SdcButton.IsEnabled = false;
            PlannerStatus.Text = snapshot.Diagnostics.Length == 0
                ? ""
                : string.Join("\n", snapshot.Diagnostics);
            UpdateDiagram();
            if (diagramPinCount != snapshot.Pins.Length)
            {
                fitDiagram = true;
                fitWholePackage = false;
                FitDiagram();
                ResetDiagramPosition();
                diagramPinCount = snapshot.Pins.Length;
            }
            SetBusy(busy);
        }
        finally
        {
            loading = false;
        }
        RefreshClockPresentation();
    }

    public void SetBusy(bool value)
    {
        busy = value;
        if (value)
        {
            ClosePinMenu();
        }
        var editable = !value && Snapshot?.CanEdit == true;
        InterruptRows.IsEnabled = editable;
        VerilogPinPicker.IsEnabled = editable && VerilogBitPicker.SelectedItem is not null;
        HseClock.IsEnabled = SysClock.IsEnabled = BusClock.IsEnabled = editable;
        SetAnalogEditable(editable);
        PinName.IsEnabled = editable && assignments.Any(item => item.PinNumber == selectedPin);
        PinDirection.IsEnabled = PinName.IsEnabled && assignments.Any(item => item.PinNumber == selectedPin && item.Function.StartsWith("GPIO", StringComparison.Ordinal));
        PinPull.IsEnabled = PinDirection.IsEnabled;
        PinOutputType.IsEnabled = PinDirection.IsEnabled && assignments.FirstOrDefault(item => item.PinNumber == selectedPin)?.Direction != "INPUT";
        PinExtraFields.IsEnabled = PinDirection.IsEnabled;
        PackageDiagram.IsEnabled = !value;
        var uartError = UpdateUartPresentation();
        var busError = UpdateBusPresentation();
        var timerError = UpdateTimerPresentation();
        var systemError = UpdateSystemPresentation(editable);
        if (Snapshot?.VerilogSourcePath is not null)

        {
            systemError ??= Ag32PinPlanService.CustomLogicConflict(GetAnalog(), setupDraft);
        }
        if (conflicts.Length == 0)
        {
            var global = systemError ?? timerError ?? busError ?? uartError;
            ConflictStatus.Text = global ?? "";
            var shownInline = PinTab.IsSelected && (timerError is not null ? TimerInspector.IsVisible && TimerStatus.Text == timerError : busError is not null ? BusInspector.IsVisible && BusStatus.Text == busError : SelectedPort >= 0);
            ConflictStatus.Visibility = global is null || (systemError is null && shownInline) ? Visibility.Collapsed : Visibility.Visible;
        }
        SavePlanButton.IsEnabled = editable && conflicts.Length == 0 && clockError is null && uartError is null && busError is null && timerError is null && systemError is null;
        ReloadPlanButton.IsEnabled = !value;
        RecommendedClocks.IsEnabled = editable && RecommendedClocks.Items.Count != 0;
        ApplyRecommendedClockButton.IsEnabled = editable && conflicts.Length == 0 && uartError is null && busError is null && timerError is null && systemError is null && RecommendedClocks.SelectedItem is Ag32ClockRecommendation;
        UpdateTimingPresentation();
    }

    public void Clear()
    {
        ClosePinMenu();
        Snapshot = null;
        selectedPin = null;
        HasChanges = false;
        diagramPinCount = 0;
        ResetDiagramPosition();
        UartPort.SelectedItem = PinProperties;
        PinFilter.Clear();
        assignments.Clear();
        conflicts = [];
        timing = null;
        clockError = null;
        externalDraft = false;
        ConflictStatus.Text = "";
        ConflictStatus.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    public Ag32PinAssignment[] GetAssignments() => assignments.Select(p => p with
    {
        InterruptPriority = p.Interrupt == "NONE" ? 1 : VectorPriority(p.Function.Split('_')[0])
    }).ToArray();

    public Ag32PinClockSettings GetClocks() => new(
        ParseClock(HseClock.Text, "HSECLK"),
        ParseClock(SysClock.Text, "SYSCLK"),
        ParseClock(BusClock.Text, "BUSCLK"));

    public void ShowResult(Ag32PinPlanResult result)
    {
        SetSnapshot(result.Snapshot);
        uartResult = result.Snapshot.Uarts is { Length: > 0 } ? "已生成 src/UART.c 和 include/UART.h，main() 调用 SX_UART_Init()。" : "已保存引脚配置。";
        UpdateUartPresentation();
        vexPath = result.VexPath;
        sdcPath = result.SdcPath;
        var modules = string.Join("\n", result.UpdatedFiles.Where(path => path.StartsWith("src/", StringComparison.Ordinal) ||
            path.StartsWith("include/", StringComparison.Ordinal)));
        var sourceDetails = modules.Length == 0 ? "" : "\n更新的工程源码：\n" + modules;
        ConstraintDetails.Text = $"VE：{result.Snapshot.SourcePath}{sourceDetails}\n系统头文件：{Ag32SystemSupport.HeaderPath}\n系统实现：{Ag32SystemSupport.SourcePath}\n引脚约束数据：{result.VexPath}\n时钟约束：{result.SdcPath}\n\n最终 ASF 和映射镜像由顶部编译生成。";
        VexButton.IsEnabled = SdcButton.IsEnabled = true;
        PlannerStatus.Text = "已保存配置并生成系统代码与约束。";
    }

    public void ShowFailure(string message)
    {
        uartResult = PlannerStatus.Text = message;
        UpdateUartPresentation();
    }

    private void Diagram_PinSelected(object? sender, int number)
    {
        if (Snapshot is null || busy)
        {
            return;
        }
        ClosePinMenu();
        selectedPin = number;
        PackageDiagram.SetSelection(number);
        AssignmentsGrid.SelectedItem = assignments.FirstOrDefault(item => item.PinNumber == number);
        if (AssignmentsGrid.SelectedItem is Ag32PinAssignment || !Equals(UartPort.SelectedItem, VerilogModule))

        {
            SelectInspectorForPin((AssignmentsGrid.SelectedItem as Ag32PinAssignment)?.Function);
        }
        UpdatePinDetails();
        var snapshot = Snapshot;
        var pin = snapshot.Pins.Single(pin => pin.Number == number);
        try
        {
            if (UsbPad(number) is not null && AssignmentsGrid.SelectedItem is null)
            {
                UartPort.SelectedItem = UsbPad(number) == "RTC_OUT" ? "RTC" : "USB0";
                return;
            }
            var analog = GetAnalog();
            var analogPin = Ag32PinPlanningService.GetReservedAnalogPins(snapshot.DeviceId, analog, setupDraft).FirstOrDefault(item => item.Pin == number);
            if (analogPin is not null && !assignments.Any(item => item.PinNumber == number))
            {
                OpenAnalogPin(number);
                PinDetails.Text = $"PIN_{number} · {analogPin.ActiveFunctions(analog,setupDraft)} · 模拟外设已预留";
                return;
            }
        }
        catch (StudioXException error)
        {
            PlannerStatus.Text = error.Message;
            return;
        }
        var anchor = PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, number));
        var menuGeneration = pinMenuGeneration;
        var menuFunctions = snapshot.Functions.Length <= 350 ? snapshot.Functions : snapshot.Functions.Where(function =>
            !function.IsVerilog || function.Name == VerilogBitPicker.SelectedValue as string ||
            assignments.Any(item => item.PinNumber == number && item.Function == function.Name)).ToArray();
        var menu = new Ag32PinFunctionMenu(anchor, pin, menuFunctions, assignments, snapshot.CanEdit,
            function =>
            {
                if (menuGeneration == pinMenuGeneration)
                {
                    AssignPin(snapshot, pin, function);
                }
            }, analogPinout.Any(p => p.Pin == number) ? () => OpenAnalogPin(number) : null);
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
            function is not null && previous.Length == 1 && previous[0].Function == function.Name &&
            (!function.IsVerilog || previous[0].Direction == function.Direction))
        {
            return;
        }
        // 跨 MCU/Verilog 的占用保留在草稿中，由共用冲突检查提示，不覆盖原配置。
        var keepOccupant = function is not null && previous.Any(item => item.Function != function.Name &&
            (snapshot.Functions.FirstOrDefault(key => key.Name == item.Function)?.IsVerilog == true) != function.IsVerilog);
        foreach (var assignment in previous.Where(item => !keepOccupant || item.Function == function!.Name))

        {
            assignments.Remove(assignment);
        }
        if (function is not null)
        {
            var name = keepOccupant ? null : previous.FirstOrDefault()?.Name;
            var added = new Ag32PinAssignment(function.Name, pin.Number, function.IsVerilog ? function.Direction : null, name);
            assignments.Add(added);
            AssignmentsGrid.SelectedItem = added;
        }
        selectedPin = pin.Number;
        SelectInspectorForPin(function?.Name);
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
        if (Snapshot.VerilogSourcePath is not null)
        {
            var functions = Snapshot.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            conflicts = [.. conflicts, .. assignments.Where(pin => !functions.Contains(pin.Function) ||
                !Snapshot.Pins.Any(packagePin => packagePin.CanAssign && packagePin.Number == pin.PinNumber))
                .Select(pin => new Ag32PinPlanConflict("VerilogPin",
                    $"{pin.Function} → PIN_{pin.PinNumber} 不在当前端口或可分配引脚列表中，请清除或改配。", [pin.PinNumber], [pin.Function]))];
        }
        var analogPins = new Dictionary<int, string>();
        try
        {
            var analog = GetAnalog();
            analogPins = Ag32PinPlanningService.GetReservedAnalogPins(Snapshot.DeviceId, analog, setupDraft)
                .ToDictionary(pin => pin.Pin, pin => pin.ActiveFunctions(analog, setupDraft));
            conflicts = [.. conflicts, .. analogPins.Where(pin => assignments.Any(item => item.PinNumber == pin.Key))
                .Select(pin => new Ag32PinPlanConflict("analog", $"PIN_{pin.Key} 的数字分配与 {pin.Value} 冲突。", [pin.Key], [pin.Value]))];
        }
        catch (StudioXException error)
        {
            conflicts = [.. conflicts, new("analog", error.Message, [], [])];
        }
        if (setupDraft.Units.ContainsKey("USB0"))
        {
            var dm = Ag32Setup.UsbDmPin(Snapshot.DeviceId);
            conflicts = [.. conflicts, .. assignments.Where(p => p.PinNumber == dm || p.PinNumber == dm + 1)
                .Select(p => new Ag32PinPlanConflict("usb", $"PIN_{p.PinNumber} 的 {p.Function} 与 USB 固定数据脚冲突。",
                    [p.PinNumber], [p.Function]))];
        }
        ConflictStatus.Text = conflicts.Length == 0 ? "" : (Snapshot.CanEdit
            ? "配置冲突，修正后才能保存：\n"
            : "VE 包含分配冲突，请先在文本编辑器修正并重新读取：\n")
            + string.Join("\n", conflicts.Select(conflict => conflict.Message));
        ConflictStatus.Visibility = conflicts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshUartPins();
        RefreshBusPins();
        RefreshTimerPins();
        RefreshVerilogPins();
        PackageDiagram.SetPins(Snapshot.Pins.Select(pin => new Ag32PackagePinVisual(pin.Number, pin.CanAssign,
            UsbPad(pin.Number) ?? analogPins.GetValueOrDefault(pin.Number) ??
            string.Join(", ", assignments.Where(item => item.PinNumber == pin.Number).Select(item => item.Name is { } name ? name + " · " + Ag32PinFunctionLabels.Compact(item.Function) : Ag32PinFunctionLabels.Compact(item.Function))),
            ConflictForPin(pin.Number))
        {
            FixedPin = pin.FixedPin,
            AlternateFunction = pin.AlternateFunction
        }).ToArray(),
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
            uartResult = null;
            PlannerStatus.Text = "有未保存的更改。";
            VexButton.IsEnabled = SdcButton.IsEnabled = false;
            RefreshClockPresentation();
        }
    }

    private void UpdatePinDetails()
    {
        if (PinName is null)
        {
            return;
        }
        var wasLoading = loading;
        loading = true;
        var selected = assignments.FirstOrDefault(item => item.PinNumber == selectedPin);
        PinEditor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        PinLegend.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        PinDirection.Visibility = selected?.Function.StartsWith("GPIO", StringComparison.Ordinal) == true ? Visibility.Visible : Visibility.Collapsed;
        AssignmentsHeading.Text = $"已分配功能（{assignments.Count}）";
        AssignmentsGrid.Visibility = PinFilterBar.Visibility = assignments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PinName.Text = selected?.Name ?? "";
        PinDirection.SelectedValue = selected?.Direction is "INPUT" or "OUTPUT" ? selected.Direction : "";
        PinPull.SelectedValue = selected?.Pull ?? "NONE";
        PinOutputType.SelectedValue = selected?.OutputType ?? "PUSH_PULL";
        PinLevel.SelectedIndex = selected?.InitialHigh == true ? 1 : 0;
        PinSlew.IsChecked = selected?.SlewControl == true;
        PinInterrupt.SelectedValue = selected?.Interrupt ?? "NONE";
        PinOutputFields.Visibility = selected?.Direction == "OUTPUT" && PinDirection.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        PinInputFields.Visibility = selected?.Direction == "INPUT" && PinDirection.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        PinName.IsEnabled = !busy && Snapshot?.CanEdit == true && selected is not null;
        PinDirection.IsEnabled = PinName.IsEnabled && selected!.Function.StartsWith("GPIO", StringComparison.Ordinal);
        PinPull.IsEnabled = PinDirection.IsEnabled;
        PinOutputType.IsEnabled = PinDirection.IsEnabled && selected?.Direction != "INPUT";
        PinExtraFields.IsEnabled = PinDirection.IsEnabled;
        var triggered = selected?.Interrupt is not (null or "NONE");
        PinInterruptHint.Visibility = triggered ? Visibility.Visible : Visibility.Collapsed;
        if (triggered)
        {
            PinInterruptHint.Text = $"事件计数  StudioX_{selected!.Function}_InterruptCount" +
                        (selected.Interrupt.EndsWith("LEVEL", StringComparison.Ordinal) ? "。电平解除后，通过 GPIO_EnableInt 重新使能此脚。" : "");
        }
        loading = wasLoading;
        var pin = Snapshot?.Pins.FirstOrDefault(pin => pin.Number == selectedPin);
        var functions = string.Join(", ", assignments.Where(item => item.PinNumber == pin?.Number).Select(item => Ag32PinFunctionLabels.Display(item.Function)));
        PinDetails.Text = pin is null ? "点击封装引脚分配功能，或选择 UART 配置串口。"
            : pin.FixedPin is { } fixedPin ? $"PIN_{pin.Number} · {fixedPin.Name} · {fixedPin.Description} · 固定连接"
            : !pin.CanAssign ? $"PIN_{pin.Number} · 固定 / 不可映射"
            : $"PIN_{pin.Number} · " + (functions.Length == 0 ? pin.AlternateFunction ?? "空闲" : functions);
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

    private bool MatchesPinFilter(object value)
    {
        if (value is not Ag32PinAssignment pin)
        {
            return false;
        }
        var filter = PinFilter.Text.Trim();
        return pin.Function.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            $"PIN_{pin.PinNumber}".Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            pin.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void PropertyWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 列表与属性各自滚动。短窗口给列表留出可点选的行，同时为属性保留编辑空间。
        var height = e.NewSize.Height;
        AssignmentsArea.MaxHeight = Math.Min(height * 0.65, Math.Max(145, height * 0.5));
    }

    private void PinFilter_Changed(object sender, TextChangedEventArgs e) =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(assignments).Refresh();

    private void Clock_Changed(object sender, TextChangedEventArgs e) => MarkChanged();
    private void PinName_Changed(object sender, TextChangedEventArgs e) => ChangePinDetails();
    private void PinDirection_Changed(object sender, SelectionChangedEventArgs e) => ChangePinDetails();
    private void PinSlew_Changed(object sender, RoutedEventArgs e) => ChangePinDetails();
    private void ChangePinDetails()
    {
        if (loading || busy || Snapshot?.CanEdit != true || assignments.FirstOrDefault(item => item.PinNumber == selectedPin) is not { } old)
        {
            return;
        }
        var name = PinName.Text.Trim();
        var direction = PinDirection.SelectedValue as string;
        var gpio = old.Function.StartsWith("GPIO", StringComparison.Ordinal);
        var updated = old with
        {
            Name = name.Length == 0 ? null : name,
            Direction = gpio ? string.IsNullOrEmpty(direction) ? null : direction : old.Direction,
            Pull = gpio ? PinPull.SelectedValue as string ?? "NONE" : old.Pull,
            OutputType = gpio ? direction == "INPUT" ? "PUSH_PULL" : PinOutputType.SelectedValue as string ?? "PUSH_PULL" : old.OutputType,
            InitialHigh = gpio && direction == "OUTPUT" && PinLevel.SelectedIndex == 1,
            SlewControl = gpio && direction == "OUTPUT" && PinSlew.IsChecked == true,
            Interrupt = gpio && direction == "INPUT" ? PinInterrupt.SelectedValue as string ?? "NONE" : "NONE"
        };
        if (updated == old)
        {
            return;
        }
        loading = true;
        assignments[assignments.IndexOf(old)] = updated;
        AssignmentsGrid.SelectedItem = updated;
        loading = false;
        MarkChanged();
        UpdateDiagram();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && Snapshot?.CanEdit == true && conflicts.Length == 0 && clockError is null)
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
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0 || value == 0 && field != "BUSCLK")
        {
            throw new StudioXException("AG32_PIN_PLAN_CLOCK", field + " 请填写正数 MHz，或留空沿用未显式配置状态。");
        }
        return value;
    }
}
