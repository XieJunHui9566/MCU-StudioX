namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

/// <summary>声明式工具的无模态窗口；关闭按钮只隐藏视图，插件退出才释放窗口和会话引用。</summary>
internal sealed class PluginToolWindow : Window
{
    private bool stopping;

    internal PluginToolWindow(Window owner, string title, ContentControl content)
    {
        Owner = owner;
        Title = title + " · MCU StudioX";
        Content = content;
        Width = 830;
        Height = 850;
        MinWidth = 420;
        MinHeight = 600;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Text");
    }

    internal void ShowOrActivate()
    {
        if (stopping)
        {
            return;
        }
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    internal void CloseSession()
    {
        stopping = true;
        Content = null;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!stopping)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
