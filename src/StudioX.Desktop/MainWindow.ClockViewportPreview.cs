namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;

public partial class MainWindow
{
    private async Task RenderExtendedClockPreviewAsync(string directory, Func<PluginMcuConfigurationView> view, Func<Func<bool>, Task> wait, Func<Task> layout, Action<bool, string> check)
    {
        void Select(string id, string value)
        {
            foreach (var category in PluginDescendants(view()).OfType<Expander>())
            {
                category.IsExpanded = true;
            }
            view().UpdateLayout();
            var combo = PluginDescendants(view()).OfType<ComboBox>().Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == id);
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, value));
        }
        view().SelectPage(1);
        await layout();
        var position = CheckClockViewport(view(), check);
        Select("clockSource", "IRC");
        Select("clockMHz", "24");
        Select("systemDivider", "2");
        await wait(() => view().IsEnabled && view().State.Clocks.Single(node => node.Id == "SYSCLK").Value == "12 MHz");
        check(PluginDescendants(view()).OfType<PluginMcuDiagramViewport>().Single() is var updated && updated.Zoom == position.Zoom && updated.Pan == position.Pan, "extended clock graph preserves the viewport after parameter updates");
        view().SelectPage(0);
        await layout();
        Select("reserve", "none");
        Select("timer2", "12");
        Select("uartRoute", "p16");
        Select("uartClock", "T1-1");
        await wait(() => view().IsEnabled && view().State.Fields.Single(field => field.Id == "uartClock").Value == "T1-1");
        view().SelectPage(1);
        await layout();
        check(view().State.Clocks.Single(node => node.Id == "T2").Enabled && view().State.Clocks.Single(node => node.Id == "T2_CLK").Value == "1 MHz", "independent Timer2 settings reach the displayed clock tree");
        check(view().State.Clocks.Single(node => node.Id == "UART1").Enabled && view().State.Clocks.Single(node => node.Id == "UART1").ActiveInputIds?.SequenceEqual(["T1"]) == true, "UART1 tree follows the selected Timer1 baud generator");
        check(PluginDescendants(view()).OfType<PluginMcuDiagramViewport>().Single() is var restored && restored.Zoom == position.Zoom && restored.Pan == position.Pan, "extended graph remembers viewport while configuring peripherals");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await layout();
            var viewport = PluginDescendants(view()).OfType<PluginMcuDiagramViewport>().Single();
            viewport.ResetView();
            await layout();
            var diagram = PluginDescendants(view()).OfType<PluginMcuClockDiagram>().Single();
            var bounds = diagram.TransformToAncestor(viewport).TransformBounds(new Rect(diagram.RenderSize));
            check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= viewport.ActualWidth && bounds.Bottom <= viewport.ActualHeight, "extended peripheral clock tree fits in " + theme.Id);
            Render(this, Path.Combine(directory, "extended-clocks-" + theme.Id + ".png"));
            viewport.SetZoom(0.85, new Point(200, 160));
            await layout();
            Render(viewport, Path.Combine(directory, "extended-clock-detail-" + theme.Id + ".png"));
        }
        PluginDescendants(view()).OfType<Button>().Single(button => Equals(button.Content, "保存配置 ●")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await wait(() => view().IsEnabled && !view().State.Dirty);
        check(true, "isolated extended preview saves its draft before closing");
    }

    private static (double Zoom, Vector Pan) CheckClockViewport(PluginMcuConfigurationView view, Action<bool, string> check)
    {
        var viewport = PluginDescendants(view).OfType<PluginMcuDiagramViewport>().Single();
        var diagram = PluginDescendants(view).OfType<PluginMcuClockDiagram>().Single();
        var anchor = new Point(160, 180);
        var coordinate = diagram.TransformToAncestor(viewport).Inverse!.Transform(anchor);
        viewport.SetZoom(1.1, anchor);
        check((diagram.TransformToAncestor(viewport).Inverse!.Transform(anchor) - coordinate).Length < 0.01, "clock zoom keeps the diagram coordinate under the pointer fixed");
        var zoom = viewport.Zoom;
        viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        check(viewport.Zoom > zoom, "mouse wheel zooms the clock canvas");
        var start = viewport.Pan;
        viewport.BeginPan(anchor, diagram);
        check(!viewport.ContinuePan(anchor + new Vector(1, 1), MouseButtonState.Pressed) && viewport.Pan == start, "clock click remains a click below the drag threshold");
        check(viewport.ContinuePan(anchor + new Vector(80, 45), MouseButtonState.Pressed) && viewport.Pan == start + new Vector(80, 45), "left drag moves the clock diagram by the pointer delta");
        viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        check(!viewport.IsMouseCaptured && !viewport.ContinuePan(anchor, MouseButtonState.Pressed), "releasing the drag clears capture and pending movement");
        var field = PluginDescendants(diagram).OfType<ComboBox>().First(control => control.IsEditable);
        var input = (TextBox)field.Template.FindName("PART_EditableTextBox", field);
        start = viewport.Pan;
        viewport.BeginPan(anchor, input);
        check(!viewport.ContinuePan(anchor + new Vector(80, 45), MouseButtonState.Pressed) && viewport.Pan == start, "dragging editable frequency text does not pan the diagram");
        zoom = viewport.Zoom;
        input.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        check(viewport.Zoom == zoom, "wheel events on frequency controls are left to the control");
        field.IsDropDownOpen = true;
        check(field.IsDropDownOpen && !viewport.IsMouseCaptured, "frequency presets still open inside the clock viewport");
        field.IsDropDownOpen = false;
        viewport.SetZoom(10);
        check(viewport.Zoom == 2.5, "clock zoom has a finite upper limit");
        viewport.SetZoom(0.01);
        check(viewport.Zoom == 0.15, "large clock trees can zoom out to fifteen percent");
        viewport.SetZoom(0.8, anchor);
        return (viewport.Zoom, viewport.Pan);
    }
}
