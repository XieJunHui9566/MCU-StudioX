namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Markup;
using StudioX.Application;

/// <summary>将共享的逻辑尺寸转换为 WPF 网格长度；x:Static 的 double 不能直接赋给 GridLength。</summary>
public sealed class WorkbenchLengthExtension(string name) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new GridLength(name switch
    {
        nameof(WorkbenchVisuals.TitleHeight) => WorkbenchVisuals.TitleHeight,
        nameof(WorkbenchVisuals.MenuHeight) => WorkbenchVisuals.MenuHeight,
        nameof(WorkbenchVisuals.ToolbarHeight) => WorkbenchVisuals.ToolbarHeight,
        nameof(WorkbenchVisuals.StatusHeight) => WorkbenchVisuals.StatusHeight,
        nameof(WorkbenchVisuals.ActivityWidth) => WorkbenchVisuals.ActivityWidth,
        nameof(WorkbenchVisuals.ProjectWidth) => WorkbenchVisuals.ProjectWidth,
        nameof(WorkbenchVisuals.UtilityWidth) => WorkbenchVisuals.UtilityWidth,
        _ => throw new ArgumentException("未知工作台尺寸：" + name, nameof(name))
    });
}
