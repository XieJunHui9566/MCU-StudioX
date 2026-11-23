namespace StudioX.Desktop;

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StudioX.Engine;

public partial class Ag32PinPlanningView
{
    private sealed class NavigationSection(string title) : DependencyObject
    {
        public string Title { get; } = title;
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
            nameof(IsExpanded), typeof(bool), typeof(NavigationSection), new PropertyMetadata(false));
        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }
    }

    private sealed class NavigationLevel(Func<string, NavigationSection> section) : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture) => section((string)item);
    }

    private readonly Dictionary<string, NavigationSection> navigationSections = [];
    private double navigationWidth = 158;

    private static (string Category, string Family) NavigationPath(string name)
    {
        if (name == PinProperties || name == VerilogModule)
        {
            return ("", "");
        }
        if (name == AnalogModule)
        {
            return ("模拟外设", "");
        }
        if (name.StartsWith("ADC") || name.StartsWith("DAC") || name.StartsWith("CMP"))
        {
            return ("模拟外设", name[..3]);
        }
        if (name.StartsWith("EXT_INT"))
        {
            return ("中断与 DMA", "外部中断");
        }
        if (name.StartsWith("LOCAL_INT"))
        {
            return ("中断与 DMA", "内核直连");
        }
        if (name == "DMA_IRQ")
        {
            return ("中断与 DMA", "DMA 向量");
        }
        if (name.StartsWith("DMA_CH"))
        {
            return ("中断与 DMA", "DMA 通道");
        }
        if (name is "USB0" or "MAC0")
        {
            return ("通信外设", name == "USB0" ? "USB FS" : "以太网");
        }
        if (name is "FLASH" or "FCB0" or HyperRamModule)
        {
            return ("系统外设", "存储与配置");
        }
        if (name is "CRC0" or "BKP")
        {
            return ("系统外设", "校验与备份");
        }
        if (name is "RTC" or "WDOG" or "IWDG" or "PWR")
        {
            return ("系统外设", "时钟与监控");
        }
        if (Ag32TimerInitialization.IsBasic(name))
        {
            return ("定时器", "基本定时器");
        }
        if (Ag32TimerInitialization.IsGeneral(name))
        {
            return ("定时器", "通用定时器");
        }
        return ("通信外设", name.StartsWith("I2C", StringComparison.Ordinal) ? "I²C" : name[..^1]);
    }

    private NavigationSection Section(string title)
    {
        if (!navigationSections.TryGetValue(title, out var section))

        {
            navigationSections.Add(title, section = new(title));
        }
        return section;
    }

    private ListCollectionView CreateNavigation(string[] modules)
    {
        var view = new ListCollectionView(new[] { PinProperties }.Concat(modules).ToArray());
        view.GroupDescriptions.Add(new NavigationLevel(name => Section(NavigationPath(name).Category)));
        view.GroupDescriptions.Add(new NavigationLevel(name => Section(NavigationPath(name).Family)));
        return view;
    }

    private void RevealNavigation(string? name)
    {
        if (name is null || name == PinProperties)
        {
            return;
        }
        var (category, family) = NavigationPath(name);
        Section(category).IsExpanded = true;
        Section(family).IsExpanded = true;
    }
}
