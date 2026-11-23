namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private sealed record UartDraft(bool Enabled, string Baud, Ag32UartSettings Settings);
    private sealed record PinChoice(int Number, string Label);
    private readonly Dictionary<int, UartDraft> uartDrafts = [];
    private bool updatingUart;
    private string? uartResult;

    private const string PinProperties = "引脚属性";
    private int SelectedPort => UartPort.SelectedItem is string module && module.StartsWith("UART", StringComparison.Ordinal) ? module[^1] - '0' : -1;
    private bool fitDiagram = true;
    private bool fitWholePackage;
    private bool fittingDiagram;
    private Point? panStart;
    private Vector panOffset;

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 930;
        if (wide != (WideNavigation.Visibility == Visibility.Visible))
        {
            if (!wide)
            {
                navigationWidth = NavigationColumn.Width.Value;
            }
            NavigationColumn.MinWidth = wide ? 114 : 0;
            NavigationColumn.Width = new GridLength(wide ? navigationWidth : 0);
            NavigationHandleColumn.Width = new GridLength(wide ? 8 : 0);
            WideNavigation.Visibility = NavigationSplitter.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }
        if (wide)
        {
            NavigationColumn.MaxWidth = e.NewSize.Width - 376;
        }
        CompactNavigation.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        UartTitle.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        var stacked = e.NewSize.Width < 490;
        var rows = PinWorkspace.RowDefinitions;
        if (stacked != (PaneSplitter.ResizeDirection == GridResizeDirection.Rows))
        {
            Grid.SetColumn(PropertyPane, stacked ? 0 : 2);
            Grid.SetColumn(PaneSplitter, stacked ? 0 : 3);
            Grid.SetColumn(PackagePane, stacked ? 0 : 4);
            foreach (var pane in new FrameworkElement[] { PropertyPane, PaneSplitter, PackagePane })

            {
                Grid.SetColumnSpan(pane, stacked ? PinWorkspace.ColumnDefinitions.Count : 1);
            }
            Grid.SetRow(PaneSplitter, stacked ? 1 : 0);
            Grid.SetRow(PackagePane, stacked ? 2 : 0);
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[1].Height = new GridLength(stacked ? 8 : 0);
            rows[2].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            PaneSplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        }
        PropertiesColumn.MinWidth = DiagramColumn.MinWidth = stacked ? 0 : 180;
    }

    private void FitPackage_Click(object sender, RoutedEventArgs e)
    {
        fitDiagram = true;
        fitWholePackage = true;
        FitDiagram();
        ResetDiagramPosition();
    }

    private void DiagramViewport_SizeChanged(object sender, SizeChangedEventArgs e) => FitDiagram();
    private Point ViewportCenter => new(DiagramViewport.ViewportWidth / 2, DiagramViewport.ViewportHeight / 2);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter, 1.3);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter, 1 / 1.3);

    private void DiagramViewport_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }
        ZoomAt(e.GetPosition(DiagramViewport), Math.Pow(1.2, e.Delta / 120d));
        e.Handled = true;
    }

    private void ZoomAt(Point anchor, double factor)
    {
        var diagramPoint = DiagramViewport.TranslatePoint(anchor, PackageDiagram);
        DiagramZoom.Value = Math.Clamp(DiagramZoom.Value * factor, DiagramZoom.Minimum, DiagramZoom.Maximum);
        DiagramViewport.UpdateLayout();
        var shift = PackageDiagram.TranslatePoint(diagramPoint, DiagramViewport) - anchor;
        PlaceDiagram(DiagramPosition - shift);
    }

    private Vector DiagramPosition => new(DiagramTranslation.X - DiagramViewport.HorizontalOffset,
        DiagramTranslation.Y - DiagramViewport.VerticalOffset);

    private void ResetDiagramPosition()
    {
        EndDiagramPan();
        DiagramTranslation.X = DiagramTranslation.Y = 0;
        DiagramViewport.ScrollToHome();
    }

    private void PlaceDiagram(Vector position)
    {
        // 图比视口大时先移动原生滚动条。在滚动范围之外也允许拖动，边缘保留可抓取的区域。
        var x = Math.Clamp(-position.X, 0, DiagramViewport.ScrollableWidth);
        var y = Math.Clamp(-position.Y, 0, DiagramViewport.ScrollableHeight);
        DiagramViewport.ScrollToHorizontalOffset(x);
        DiagramViewport.ScrollToVerticalOffset(y);
        DiagramTranslation.X = EdgeOffset(position.X + x, DiagramViewport.ViewportWidth, DiagramHost.ActualWidth);
        DiagramTranslation.Y = EdgeOffset(position.Y + y, DiagramViewport.ViewportHeight, DiagramHost.ActualHeight);

        static double EdgeOffset(double offset, double viewport, double size)
        {
            var limit = Math.Max(0, Math.Min(viewport, (size + viewport) / 2) - 32);
            return Math.Clamp(offset, -limit, limit);
        }
    }

    private void DiagramSurface_PanStart(object sender, MouseButtonEventArgs e)
    {
        panStart = e.GetPosition(DiagramViewport);
        panOffset = DiagramPosition;
    }

    private void DiagramSurface_PanMove(object sender, MouseEventArgs e)
    {
        if (panStart is not { } start)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDiagramPan();
            return;
        }
        var delta = e.GetPosition(DiagramViewport) - start;
        if (!DiagramSurface.IsMouseCaptured)
        {
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            // 在引脚按钮处理抬起之前接管拖动；普通单击仍交给原来的按钮或标签。
            if (!DiagramSurface.CaptureMouse())
            {
                return;
            }
            DiagramSurface.Cursor = Cursors.ScrollAll;
        }
        PlaceDiagram(panOffset + delta);
        e.Handled = true;
    }

    private void DiagramSurface_PanEnd(object sender, MouseButtonEventArgs e)
    {
        if (DiagramSurface.IsMouseCaptured)
        {
            e.Handled = true;
        }
        EndDiagramPan();
    }

    private void DiagramSurface_PanLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DiagramSurface))
        {
            EndDiagramPan();
        }
    }

    private void DiagramSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!DiagramSurface.IsMouseCaptureWithin)
        {
            EndDiagramPan();
        }
    }

    private void EndDiagramPan()
    {
        panStart = null;
        if (DiagramSurface.IsMouseCaptured)
        {
            DiagramSurface.ReleaseMouseCapture();
        }
        DiagramSurface.Cursor = Cursors.Hand;
    }

    private void DiagramZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded && !fittingDiagram)
        {
            fitDiagram = false;
        }
        SizeDiagramHost();
    }

    private void FitDiagram()
    {
        if (DiagramViewport.ActualWidth < 1 || DiagramViewport.ActualHeight < 1 ||
            PackageDiagram.Width is not > 0 || PackageDiagram.Height is not > 0)
        {
            return;
        }
        fittingDiagram = true;
        try
        {
            if (fitDiagram)
            {
                var fit = 100 * Math.Min((DiagramViewport.ActualWidth - 4) / PackageDiagram.Width,
                    (DiagramViewport.ActualHeight - 4) / PackageDiagram.Height);
                // 初次打开时保留可读的脚号；用户明确选择全图后，才允许随视口继续缩小。
                DiagramZoom.Value = Math.Clamp(fit, fitWholePackage ? DiagramZoom.Minimum : 90, DiagramZoom.Maximum);
            }
            SizeDiagramHost();
        }
        finally
        {
            fittingDiagram = false;
        }
    }

    private void SizeDiagramHost()
    {
        if (DiagramHost is null || PackageDiagram.Width is not > 0 || PackageDiagram.Height is not > 0)
        {
            return;
        }
        DiagramHost.Width = PackageDiagram.Width * DiagramZoom.Value / 100;
        DiagramHost.Height = PackageDiagram.Height * DiagramZoom.Value / 100;
    }

    private void SelectInspectorForPin(string? function)
    {
        if (function is not null && Snapshot?.Functions.Any(item => item.IsVerilog && item.Name == function) == true)
        {
            UartPort.SelectedItem = VerilogModule;
            SelectVerilogSignal(function);
            return;
        }
        var module = function?.StartsWith("EXT_INT", StringComparison.Ordinal) == true ? function : function?.Split('_')[0];
        UartPort.SelectedItem = module is not null && UartPort.Items.Contains(module) ? module : PinProperties;
        RevealNavigation(UartPort.SelectedItem as string);
        if (SelectedTimer is not null && function is not null && function.Contains("_CH", StringComparison.Ordinal) && function[^1] is >= '0' and <= '3')

        {
            TimerChannel.SelectedIndex = function[^1] - '0';
        }
    }

    private void UartPreview_Click(object sender, RoutedEventArgs e)
    {
        var show = UartPreview.Visibility != Visibility.Visible;
        UartPreview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UartPreviewButton.Content = show ? "收起代码" : "预览初始化代码";
        if (show)
        {
            UartPreview.BringIntoView(new Rect(0, 0, 1, 48));
        }
    }

    private void PlannerTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PlannerTabs) || Snapshot is null || loading)
        {
            return;
        }
        if (IrqTab.IsSelected)
        {
            RefreshInterruptRows();
        }
        if (PinTab.IsSelected)
        {
            ShowUartPort();
            UpdatePinDetails();
        }
        SetBusy(busy);
    }

    private void LoadUarts(Ag32PinPlanSnapshot snapshot)
    {
        updatingUart = true;
        var previous = UartPort.SelectedItem as string;
        uartResult = null;
        uartDrafts.Clear();
        foreach (var item in snapshot.Uarts ?? [])

        {
            uartDrafts[item.Port] = new(true, item.Baud.ToString(CultureInfo.InvariantCulture), item);
        }
        var functions = snapshot.Functions.Where(f => !f.IsVerilog).ToArray();
        var modules = functions.Where(f => f.Name.EndsWith("_UARTTXD", StringComparison.Ordinal) ||
            f.Name.EndsWith("_UARTRXD", StringComparison.Ordinal)).Select(f => f.Name.Split('_')[0])
            .Where(name => name.Length == 5 && name.StartsWith("UART", StringComparison.Ordinal) && name[4] is >= '0' and <= '4')
            .Distinct().Order().ToArray();
        LoadSystemDraft(snapshot.SystemPeripherals);
        LoadBusDrafts(snapshot);
        LoadTimerDrafts(snapshot);
        modules = [.. modules, .. functions.Select(f => f.Name.Split('_')[0])
            .Where(Ag32BusInitialization.Supports).Distinct().OrderBy(name => name[..^1] switch { "SPI" => 0, "I2C" => 1, _ => 2 }).ThenBy(name => name, StringComparer.Ordinal)];
        modules = [.. modules, .. functions.Select(f => f.Name.Split('_')[0]).Where(Ag32TimerInitialization.IsGeneral)
            .Distinct().Order(), "TIMER0.1", "TIMER0.2", "TIMER1.1", "TIMER1.2", AnalogModule, "RTC", "WDOG", .. Ag32Setup.Catalog.Keys.Where(n=>!Ag32TimerInitialization.IsGeneral(n) && n != HyperRamModule)];
        if (Ag32DeviceCatalog.Require(snapshot.DeviceId).ExternalPsramBytes > 0)
        {
            modules = [.. modules, HyperRamModule];
        }
        if (snapshot.VerilogSourcePath is not null)
        {
            modules = [VerilogModule, .. modules];
        }
        LoadVerilogPorts(snapshot);
        UartPort.ItemsSource = CreateNavigation(modules.Distinct().ToArray());
        UartPort.SelectedItem = modules.Contains(previous) ? previous : snapshot.VerilogSourcePath is not null ? VerilogModule : PinProperties;
        updatingUart = false;
        ShowUartPort();
    }

    private void ShowUartPort()
    {
        var uart = SelectedPort >= 0;
        var bus = SelectedBus is not null;
        BusInspector.Visibility = bus ? Visibility.Visible : Visibility.Collapsed;
        var timer = SelectedTimer is not null;
        TimerInspector.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        var system = UartPort.SelectedItem is "RTC" or "WDOG";
        SystemInspector.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        RtcFields.Visibility = UartPort.SelectedItem is "RTC" ? Visibility.Visible : Visibility.Collapsed;
        WdogFields.Visibility = UartPort.SelectedItem is "WDOG" ? Visibility.Visible : Visibility.Collapsed;
        SystemTitle.Text = UartPort.SelectedItem is "RTC" ? "RTC 实时时钟" : "WDOG 系统看门狗";
        var setupPanel = ShowSetup(UartPort.SelectedItem as string);
        var analog = Equals(UartPort.SelectedItem, AnalogModule);
        AnalogInspector.Visibility = analog ? Visibility.Visible : Visibility.Collapsed;
        var verilog = Equals(UartPort.SelectedItem, VerilogModule);
        VerilogInspector.Visibility = verilog ? Visibility.Visible : Visibility.Collapsed;
        if (verilog)
        {
            RefreshVerilogPins();
        }
        PinInspector.Visibility = uart || bus || timer || system || analog || setupPanel || verilog ? Visibility.Collapsed : Visibility.Visible;
        UartInspector.Visibility = uart ? Visibility.Visible : Visibility.Collapsed;
        PropertyScroll.ScrollToTop();
        if (!uart)
        {
            PackageDiagram.ShowUartPins(null);
            if (bus)
            {
                ShowBusPort();
            }
            if (timer)
            {
                ShowTimerPort();
            }
            if (!loading)
            {
                SetBusy(busy);
            }
            return;
        }
        UartTitle.Text = $"UART{SelectedPort} 参数";
        var draft = uartDrafts.GetValueOrDefault(SelectedPort) ?? new(false, "115200", new(SelectedPort, 115200, false));
        updatingUart = true;
        UartEnabled.IsChecked = draft.Enabled;
        UartBaud.Text = draft.Baud;
        UartIrq.IsChecked = draft.Settings.ReceiveInterrupt;
        UartDataBits.SelectedItem = draft.Settings.DataBits;
        UartStopBits.SelectedItem = draft.Settings.StopBits;
        UartParity.SelectedValue = draft.Settings.Parity;
        UartFifo.IsChecked = draft.Settings.Fifo;
        UartFlow.SelectedValue = draft.Settings.FlowControl;
        UartMode.SelectedValue = draft.Settings.Direction;
        UartPriority.SelectedItem = draft.Settings.InterruptPriority;
        updatingUart = false;
        RefreshUartPins();
        SetBusy(busy);
    }

    private void RefreshUartPins()
    {
        if (Snapshot is not { } snapshot || SelectedPort < 0)
        {
            return;
        }
        updatingUart = true;
        try
        {
            var reserved = Ag32PinPlanningService.GetReservedAnalogPins(snapshot.DeviceId, GetAnalog(), setupDraft).Select(item => item.Pin).ToHashSet();
            void Bind(ComboBox picker, string function) => BindSignalPin(picker, function, reserved);
            Bind(UartTx, $"UART{SelectedPort}_UARTTXD");
            Bind(UartRx, $"UART{SelectedPort}_UARTRXD");
            Bind(UartRts, $"UART{SelectedPort}_NUARTRTS");
            Bind(UartCts, $"UART{SelectedPort}_NUARTCTS");
            PackageDiagram.ShowUartPins($"UART{SelectedPort}", SelectedPin(UartTx), SelectedPin(UartRx), SelectedPin(UartRts), SelectedPin(UartCts));
            static int? SelectedPin(ComboBox picker) => picker.SelectedValue is int n && n > 0 ? n : null;
        }
        catch (StudioXException)
        {
            // 模拟引脚草稿无效时，沿用原页冲突说明并暂停更换数字引脚。
            UartTx.ItemsSource = UartRx.ItemsSource = UartRts.ItemsSource = UartCts.ItemsSource = null;
        }
        finally
        {
            updatingUart = false;
        }
    }

    private void UartPort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !updatingUart)
        {
            RevealNavigation(UartPort.SelectedItem as string);
            ShowUartPort();
        }
    }

    private void UartPin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || updatingUart || busy || Snapshot?.CanEdit != true ||
            sender is not ComboBox picker || picker.SelectedValue is not int number)
        {
            return;
        }
        var function = $"UART{SelectedPort}_" + (string)picker.Tag;
        AssignPeripheralPin(function, number);
    }

    private void AssignPeripheralPin(string function, int number)
    {
        var old = assignments.FirstOrDefault(item => item.Function == function);
        if ((old?.PinNumber ?? 0) == number)
        {
            return;
        }
        if (old is not null)
        {
            assignments.Remove(old);
        }
        if (number != 0)
        {
            assignments.Add(old is null ? new(function, number) : old with
            {
                PinNumber = number
            });
        }
        selectedPin = number != 0 ? number : null;
        MarkChanged();
        UpdateDiagram();
    }

    private void UartField_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || updatingUart || busy || Snapshot?.CanEdit != true || SelectedPort < 0)
        {
            return;
        }
        var settings = new Ag32UartSettings(SelectedPort, 115200, UartIrq.IsChecked == true)
        {
            AdditionalInterruptMask = uartDrafts.GetValueOrDefault(SelectedPort)?.Settings.AdditionalInterruptMask ?? 0,
            DataBits = UartDataBits.SelectedItem as int? ?? 8,
            StopBits = UartStopBits.SelectedItem as int? ?? 1,
            Parity = UartParity.SelectedValue as string ?? "NONE",
            Fifo = UartFifo.IsChecked == true,
            InterruptPriority = UartPriority.SelectedItem as int? ?? 1,
            FlowControl = UartFlow.SelectedValue as string ?? "NONE",
            Direction = UartMode.SelectedValue as string ?? "AUTO"
        };
        uartDrafts[SelectedPort] = new(UartEnabled.IsChecked == true, UartBaud.Text, settings);
        if (ReferenceEquals(sender, UartFifo))
        {
            ShowSetup(settings.Module);
        }
        MarkChanged();
    }

    public Ag32UartSettings[] GetUarts()
    {
        var result = new List<Ag32UartSettings>();
        foreach (var (port, draft) in uartDrafts.OrderBy(pair => pair.Key))
        {
            if (!draft.Enabled)
            {
                continue;
            }
            if (!int.TryParse(draft.Baud.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var baud))

            {
                throw new StudioXException("AG32_UART_BAUD", $"UART{port} 波特率请填写正整数，例如 115200。");
            }
            result.Add(draft.Settings with
            {
                Baud = baud
            });
        }
        return result.ToArray();
    }

    private string? UpdateUartPresentation()
    {
        if (Snapshot is null || UartStatus is null)
        {
            return null;
        }
        var editable = Snapshot.CanEdit && !busy && SelectedPort >= 0;
        PeripheralList.IsEnabled = UartPort.IsEnabled = !busy;
        UartEnabled.IsEnabled = editable;
        UartFields.IsEnabled = editable;
        var managed = editable && UartEnabled.IsChecked == true;
        UartFrameFields.IsEnabled = UartIrq.IsEnabled = UartBaud.IsEnabled = UartFifo.IsEnabled = UartFlow.IsEnabled = managed;
        UartPriority.IsEnabled = managed && (UartIrq.IsChecked == true || (uartDrafts.GetValueOrDefault(SelectedPort)?.Settings.AdditionalInterruptMask ?? 0) != 0);
        var flow = UartFlow.SelectedValue as string;
        UartRtsRow.Visibility = flow is "RTS" or "RTS_CTS" || UartRts.SelectedValue is int r && r > 0 ? Visibility.Visible : Visibility.Collapsed;
        UartCtsRow.Visibility = flow is "CTS" or "RTS_CTS" || UartCts.SelectedValue is int c && c > 0 ? Visibility.Visible : Visibility.Collapsed;
        var parity = (UartParity.SelectedValue as string) switch
        {
            "ODD" => "O",
            "EVEN" => "E",
            "ONE" => "M",
            "ZERO" => "S",
            _ => "N"
        };
        UartRts.IsEnabled = UartCts.IsEnabled = editable;
        UartFrame.Header = $"帧格式与收发方向　{UartDataBits.SelectedItem}{parity}{UartStopBits.SelectedItem}";
        UartHint.Text = UartIrq.IsChecked == true && UartEnabled.IsChecked == true
            ? $"接收数据可通过 StudioX_UART{SelectedPort}_TryRead(&value) 读取。"
            : "只发送或只接收时可留空另一引脚。";
        string? error = null;
        var preview = "";
        try
        {
            foreach (var item in GetUarts())
            {
                var code = Ag32PinPlanningService.PreviewUart(item, assignments, GetClocks(), setupDraft);
                if (item.Port == SelectedPort)
                {
                    preview = code;
                }
            }
        }
        catch (StudioXException ex)
        {
            error = ex.Message;
        }
        var problem = error ?? conflicts.FirstOrDefault()?.Message ?? clockError;
        UartStatus.SetResourceReference(TextBlock.ForegroundProperty, problem is null ? "Muted" : "DiagnosticError");
        UartStatus.Text = problem ?? uartResult ?? (!Snapshot.CanEdit ? string.Join("\n", Snapshot.Diagnostics) :
            !HasChanges && UartEnabled.IsChecked != true ? "此串口由应用代码自行初始化。" : "");
        UartStatus.Visibility = UartStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UartPreview.Text = preview;
        UartPreviewButton.IsEnabled = preview.Length > 0;
        if (preview.Length == 0)
        {
            UartPreview.Visibility = Visibility.Collapsed;
            UartPreviewButton.Content = "预览初始化代码";
        }
        return error;
    }
}
