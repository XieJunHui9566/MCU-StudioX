using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Desktop;
using StudioX.Engine;

internal static class BuildMemoryViewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var view = new BuildMemoryView();
        view.SetReport(new([new("app.elf", "Release", DateTime.UtcNow,
            [new("Flash Code", 0, 0, 16384, IsLogical: true),
             new("Flash Data", 0, 0, 8192, IsLogical: true),
             new("DIRAM", 0, 341760, 53883, IsLogical: true),
             new("IRAM", 0, 16384, 16384, IsLogical: true),
             new("RTC SLOW", 0, 8192, 32, IsLogical: true),
             new("RTC FAST", 0, 8192, 24, IsLogical: true)])], "SDK 原生统计"));
        var target = ((ItemsControl)view.FindName("Targets")).ItemsSource.Cast<object>().Single();
        var regions = ((IEnumerable)target.GetType().GetProperty("Regions")!.GetValue(target)!).Cast<object>().ToArray();
        var flash = regions[0];
        string Text(object value, string name) => (string)value.GetType().GetProperty(name)!.GetValue(value)!;
        check(regions.Length == 2 && Text(regions[0], "Name") == "Flash" && Text(regions[1], "Name") == "RAM" &&
            Text(flash, "SizeText").StartsWith("24 KiB", StringComparison.Ordinal), "ESP 默认只显示 Flash/RAM 总览，Flash 合计 SDK Code/Data 占用");
        check(Text(flash, "SizeText").EndsWith("/ 未知", StringComparison.Ordinal) && Text(flash, "Percentage") == "—",
            "SDK 未提供 Flash 容量时显示未知，不伪造百分比");
        check(!Text(flash, "Description").Contains("超出容量", StringComparison.Ordinal) &&
            !Text(flash, "Description").Contains("起始地址", StringComparison.Ordinal),
            "SDK 逻辑区隐藏不存在的起始地址，未知容量不报告超限");
        check(Text(regions[1], "Percentage") != "—" && Text(regions[1], "Description").Contains("共享", StringComparison.Ordinal),
            "SDK RAM 总览使用厂商提供容量并说明共享统计");
        check(Text(regions[1], "Description").Contains("70,323 B", StringComparison.Ordinal) &&
            Text(regions[1], "Description").Contains("374,528 B", StringComparison.Ordinal),
            "RAM 合计 SDK 已归并的 DIRAM 与独立 IRAM/RTC 分组，不再次按别名地址重复计数");
        view.Measure(new Size(300, 400));
        view.Arrange(new Rect(0, 0, 300, 400));
        view.UpdateLayout();
        var details = Descendants(view).OfType<Expander>().Single(item => item.Name == "SdkDetails");
        check(details.Visibility == Visibility.Visible && !details.IsExpanded, "ESP 详细分组默认折叠，透明主题模板保持可展开");
        details.IsExpanded = true;
        view.UpdateLayout();
        var detailedRows = Descendants(view).OfType<ItemsControl>().Single(item => item.Name == "SdkDetailRows");
        check(detailedRows.Items.Count == 6 && detailedRows.ItemTemplate is not null, "展开后保留全部六项 SDK 原始分组及相同占用展示");
        view.SetReport(new([new("firmware.elf", "Release", DateTime.UtcNow,
            [new("FLASH", 0x08000000, 1024, 2048)])], "普通链接统计"));
        target = ((ItemsControl)view.FindName("Targets")).ItemsSource.Cast<object>().Single();
        var ordinary = ((IEnumerable)target.GetType().GetProperty("Regions")!.GetValue(target)!).Cast<object>().Single();
        check(Text(ordinary, "Description").Contains("0x08000000", StringComparison.Ordinal) &&
            Text(ordinary, "Description").Contains("超出容量", StringComparison.Ordinal),
            "普通 MCU 的真实起始地址与容量超限提示保留");
        check((Visibility)target.GetType().GetProperty("DetailsVisibility")!.GetValue(target)! == Visibility.Collapsed,
            "普通 MCU 继续直接显示链接区域，不新增详细分组入口");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }
}
