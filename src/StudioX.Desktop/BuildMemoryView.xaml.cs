namespace StudioX.Desktop;

using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Engine;

public partial class BuildMemoryView : UserControl
{
    public BuildMemoryView() => InitializeComponent();
    internal int RegionCount
    {
        get; private set;
    }
    public void SetMessage(string message)
    {
        Targets.ItemsSource = null;
        RegionCount = 0;
        AnalysisStatus.Text = message;
    }
    public void SetReport(BuildMemoryReport report)
    {
        RegionCount = report.Targets.Sum(target => target.Regions.Count);
        Targets.ItemsSource = report.Targets.Select(target => new
        {
            Title = target.Name + (string.IsNullOrEmpty(target.Configuration) ? "" : $" [{target.Configuration}]"),
            Description = target.BuiltAtUtc == DateTime.MinValue ? target.Name :
                $"{target.Name}\n构建时间：{target.BuiltAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                (target.Regions.Any(region => region.IsLogical)
                    ? "SDK 原生逻辑区统计；共享 RAM 按 SDK 规则归属，未提供的容量显示未知。不是运行时峰值。"
                    : "占用为加载段的地址区间并集，含堆栈预留；独立段间的空闲地址不计入。不是运行时峰值。"),
            target.Diagnostic,
            Regions = VisibleRegions(target.Regions).Select(ProjectRegion).ToArray(),
            Details = target.Regions.Select(ProjectRegion).ToArray(),
            DetailsVisibility = IsSdkReport(target.Regions) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed
        }).ToArray();
        AnalysisStatus.Text = report.Message;
    }
    private static bool IsSdkReport(IReadOnlyList<BuildMemoryRegion> regions) => regions.Count > 0 && regions.All(region => region.IsLogical);
    private static IReadOnlyList<BuildMemoryRegion> VisibleRegions(IReadOnlyList<BuildMemoryRegion> regions)
    {
        if (!IsSdkReport(regions)) { return regions; }
        var flash = regions.Where(region => region.Name.StartsWith("Flash", StringComparison.Ordinal)).ToArray();
        var ram = regions.Except(flash).ToArray();
        var overview = new List<BuildMemoryRegion>();
        if (flash.Length > 0)
        {
            // Flash Code/Data 是映射分组，不是两块 Flash；只合计占用，不把映射容量相加。
            overview.Add(new("Flash", 0, 0, Sum(flash, region => region.Used), IsLogical: true));
        }
        if (ram.Length > 0)
        {
            // SDK 已对 IRAM/DRAM 的共享地址归并为 DIRAM；这里只加厂商逻辑组，不能再按链接地址统计一次。
            overview.Add(new("RAM", 0, ram.All(region => region.Capacity > 0) ? Sum(ram, region => region.Capacity) : 0,
                Sum(ram, region => region.Used), IsLogical: true));
        }
        return overview;
    }
    private static ulong Sum(IEnumerable<BuildMemoryRegion> regions, Func<BuildMemoryRegion, ulong> selector) =>
        regions.Aggregate(0UL, (total, region) => checked(total + selector(region)));
    private static object ProjectRegion(BuildMemoryRegion region) => new
    {
        region.Name,
        Percentage = region.Capacity == 0 ? "—" : region.Percent.ToString("0.##", CultureInfo.InvariantCulture) + "%",
        BarPercent = Math.Clamp(region.Percent, 0, 100),
        Fill = new SolidColorBrush(region.Capacity == 0 ? Color.FromRgb(114, 124, 139) :
            region.Percent >= 95 ? Color.FromRgb(206, 85, 85) : region.Percent >= 80 ? Color.FromRgb(191, 140, 53) : Color.FromRgb(44, 154, 98)),
        SizeText = $"{Size(region.Used)} / " + (region.Capacity == 0 ? "未知" : Size(region.Capacity)),
        Description = DescribeRegion(region)
    };
    private static string DescribeRegion(BuildMemoryRegion region) =>
        (region.IsLogical ? region.Name + " · SDK 逻辑区" : $"{region.Name} · 起始地址 0x{region.Origin:X8}") +
        $"\n链接分配：{region.Used:N0} B / 容量：" + (region.Capacity == 0 ? "未知" : $"{region.Capacity:N0} B") + "\n" +
        (region.Capacity == 0 ? "SDK 未提供该区的容量，无法计算剩余量或判断超限。" :
            region.Used > region.Capacity ? $"超出容量：{region.Used - region.Capacity:N0} B" : $"剩余：{region.Capacity - region.Used:N0} B") +
        (region.IsLogical ? region.Name == "Flash" ? "\nSDK Flash 分组占用之和，不含引导程序、分区表等开销。" :
            region.Name == "RAM" ? "\nSDK 已归并共享 RAM；总览合计各原生逻辑组，详细分组可展开。不是运行时峰值。" :
            "\n共享地址和逻辑分组按 SDK 原生统计处理，不代表运行时堆/栈峰值。" :
            "\n含 BSS、堆栈预留和段内对齐；段间空洞不计入，不代表运行时峰值。");
    private static string Size(ulong value) => value < 1024 ? $"{value} B" : value < 1024 * 1024 ?
        (value / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB" :
        (value / (1024d * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
}
