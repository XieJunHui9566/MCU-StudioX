namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>真实语言服务与编辑器交互检查；修改仅保存在内存，不写入用户工程。</summary>
    public async Task RenderNavigationPreviewAsync(string directory, string project)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        var original = activeDocument!;
        try
        {
            SourceEditor.Document.Insert(0, "// 未保存的编辑 😀\r\n");
            var main = activeEditor!; var buffer = main.Buffer; var unsaved = SourceEditor.Text;
            var at = unsaved.IndexOf("INT_DisableIntGlobal", StringComparison.Ordinal) + 3;
            SourceEditor.CaretOffset = at; UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var view = SourceEditor.TextArea.TextView;
            view.EnsureVisualLines();
            var visual = view.GetVisualPosition(new TextViewPosition(buffer.GetLocation(at)), VisualYPosition.LineMiddle);
            var point = view.TranslatePoint(visual - view.ScrollOffset + new Vector(1, 0), SourceEditor);
            if (HitSymbol(point) != at || HitSymbol(new Point(1, point.Y)) is not null) throw new InvalidOperationException("符号/行号栏命中检查失败。");
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme); UpdateLayout();
                QueueSymbolHover(at, point); await hoverTask;
                var tip = symbolToolTip ?? throw new InvalidOperationException("悬停未显示：" + Status.Text);
                tip.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (tip.Content is not StackPanel body || !body.Children.OfType<TextBlock>().Any(label => label.Text.Contains("interrupt.h:14:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("悬停缺少准确声明位置。");
                Render(tip, Path.Combine(directory, "hover-" + theme.Id + ".png"));
                HideSymbolHover();
            }
            ApplyTheme(ThemeService.Dark);
            var menu = sourceContextMenu!;
            contextFromMouse = true; contextOffset = at;
            menu.PlacementTarget = SourceEditor; menu.IsOpen = true; menu.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(menu, Path.Combine(directory, "context-menu.png"));
            if (menu.Items[^1] is not MenuItem { IsEnabled: true }) throw new InvalidOperationException("右键编辑命令不可用。");
            if (menu.Items[1] is not MenuItem declaration || !declaration.IsEnabled) throw new InvalidOperationException("声明菜单不可用。");
            declaration.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; await navigationTask;
            if (activeDocument?.RelativePath != "device/sdk/include/interrupt.h" || SourceEditor.SelectedText != "INT_DisableIntGlobal") throw new InvalidOperationException("右键声明跳转失败：" + Status.Text);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "declaration.png"));
            await TravelNavigationAsync(backwards: true);
            if (SourceEditor.Document != buffer || SourceEditor.Text != unsaved || SourceEditor.CaretOffset != at) throw new InvalidOperationException("返回丢失未保存内容或光标。");
            await TravelNavigationAsync(backwards: false);
            if (activeDocument?.RelativePath != "device/sdk/include/interrupt.h") throw new InvalidOperationException("前进失败。");
            await TravelNavigationAsync(backwards: true);
            QueueCodeNavigation(false, SourceEditor.Text.LastIndexOf("app_heartbeat", StringComparison.Ordinal) + 2); await navigationTask;
            if (SourceEditor.SelectedText != "app_heartbeat" || SourceEditor.Document != buffer) throw new InvalidOperationException("变量同文件跳转失败。");
            QueueCodeNavigation(false, SourceEditor.Text.IndexOf("uint32_t", StringComparison.Ordinal) + 2); await navigationTask;
            if (!SourceEditor.IsReadOnly || activeDocument?.ReadOnlyReason != "只读 · 内置工具链头文件") throw new InvalidOperationException("内置头文件跳转/只读保护失败。");
            await TravelNavigationAsync(backwards: true);
            var headerSource = await services.Files.ReadAsync(project, "device/sdk/include/system.h");
            ShowSource(headerSource); var header = activeEditor!;
            header.Buffer.Insert(0, "// 未保存的头文件\n\n");
            ShowDocument(main.Tab);
            QueueCodeNavigation(true, SourceEditor.Text.IndexOf("SYS_GetDeviceID", StringComparison.Ordinal) + 2); await navigationTask;
            if (activeEditor != header || SourceEditor.SelectedText != "SYS_GetDeviceID" || SourceEditor.TextArea.Caret.Line != 276) throw new InvalidOperationException("未保存头文件跳转行号不正确。");
            await TravelNavigationAsync(backwards: true);
            QueueSymbolHover(at, point); var staleHover = hoverTask;
            SourceEditor.Document.Insert(0, "// changed\n"); await staleHover;
            if (symbolToolTip is not null) throw new InvalidOperationException("过期悬停仍显示。");
            QueueCodeNavigation(false, SourceEditor.Text.IndexOf("INT_DisableIntGlobal", StringComparison.Ordinal) + 2); var staleNavigation = navigationTask;
            ShowDocument(header.Tab); await staleNavigation;
            if (activeEditor != header) throw new InvalidOperationException("过期跳转切走了当前标签。");
            ShowDocument(main.Tab);
            if (IsSymbolContext(4)) throw new InvalidOperationException("注释中不应请求跳转。");
            SourceEditor.Undo(); SourceEditor.Undo();
            if (SourceEditor.Text != original.Text) throw new InvalidOperationException("跳转损坏撤销记录。");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: pointer hit testing, dark/light hover with declaration path/line, context-menu navigation, same-file variable definition, built-in read-only header, unsaved header range, back/forward preserving buffers/caret/undo, comments and stale-request cancellation. No user files changed.\n");
        }
        finally
        {
            CloseCodeAssistance(); sourceContextMenu!.IsOpen = false;
            await Task.WhenAll(hoverTask, navigationTask);
            ClearEditorDocuments(); ShowSource(original);
        }
    }
}
