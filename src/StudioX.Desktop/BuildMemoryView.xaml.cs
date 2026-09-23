namespace StudioX.Desktop;

using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Engine;

public partial class BuildMemoryView : UserControl
{
    public BuildMemoryView() => InitializeComponent();
    internal int RegionCount { get; private set; }
    public void SetMessage(string message)
    { Targets.ItemsSource = null; RegionCount = 0; AnalysisStatus.Text = message; }
    public void SetReport(BuildMemoryReport report)
    {
        RegionCount = report.Targets.Sum(target => target.Regions.Count);
        Targets.ItemsSource = report.Targets.Select(target => new
        {
            Title = target.Name + (string.IsNullOrEmpty(target.Configuration) ? "" : $" [{target.Configuration}]"),
            Description = target.BuiltAtUtc == DateTime.MinValue ? target.Name : $"{target.Name}\n构建时间：{target.BuiltAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n占用为加载段的地址区间并集，含堆栈预留；独立段间的空闲地址不计入。不是运行时峰值。",
            target.Diagnostic,
            Regions = target.Regions.Select(region => new
            {
                region.Name,
                Percentage = region.Capacity == 0 ? "—" : region.Percent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
                BarPercent = Math.Clamp(region.Percent, 0, 100),
                Fill = new SolidColorBrush(region.Percent >= 95 ? Color.FromRgb(206, 85, 85) : region.Percent >= 80 ? Color.FromRgb(191, 140, 53) : Color.FromRgb(44, 154, 98)),
                SizeText = $"{Size(region.Used)} / {Size(region.Capacity)}",
                Description = $"{region.Name} · 起始地址 0x{region.Origin:X8}\n链接分配：{region.Used:N0} B / 容量：{region.Capacity:N0} B\n" +
                    (region.Used > region.Capacity ? $"超出容量：{region.Used - region.Capacity:N0} B" : $"剩余：{region.Capacity - region.Used:N0} B") +
                    "\n含 BSS、堆栈预留和段内对齐；段间空洞不计入，不代表运行时峰值。"
            }).ToArray()
        }).ToArray();
        AnalysisStatus.Text = report.Message;
    }
    private static string Size(ulong value) => value < 1024 ? $"{value} B" : value < 1024 * 1024 ?
        (value / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB" :
        (value / (1024d * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
}
