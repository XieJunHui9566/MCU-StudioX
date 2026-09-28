namespace StudioX.Desktop;

using System.Diagnostics;
using System.Windows;
using StudioX.Application.Espressif;

public partial class AiSettingsWindow
{
    private readonly EspressifDocumentationService? espressifDocumentation;
    private CancellationTokenSource? espressifConnectionCancellation;
    private bool espressifSettingsClosed;

    private void RefreshEspressifState()
    {
        var status = espressifDocumentation?.GetStatus();
        EspressifState.Text = status?.Message ?? "文档服务未启用。";
        ConnectEspressifButton.IsEnabled = espressifDocumentation is not null && espressifConnectionCancellation is null;
        DisconnectEspressifButton.IsEnabled = status?.AuthenticationSaved == true && espressifConnectionCancellation is null;
        CancelEspressifButton.IsEnabled = true;
        CancelEspressifButton.Visibility = espressifConnectionCancellation is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectEspressif_Click(object sender, RoutedEventArgs e)
    {
        if (espressifDocumentation is null || espressifConnectionCancellation is not null)
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        espressifConnectionCancellation = cancellation;
        SaveButton.IsEnabled = false;
        ErrorText.Text = "";
        RefreshEspressifState();
        EspressifState.Text = "正在连接；请在浏览器完成乐鑫授权，完成后会自动返回这里。";
        try
        {
            await espressifDocumentation.ConnectAsync(OpenEspressifBrowserAsync, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 用户取消或关闭设置时，不把正常取消显示成接口故障。
        }
        catch (Exception ex)
        {
            // OAuth 异常可能含授权地址；界面只展示安全状态，原始诊断由服务处理。
            if (!espressifSettingsClosed)
            {
                ErrorText.Text = $"乐鑫文档连接失败（{ex.GetType().Name}），请查看连接状态后重试。";
            }
        }
        finally
        {
            espressifConnectionCancellation = null;
            if (!espressifSettingsClosed)
            {
                RefreshEspressifState();
                SaveButton.IsEnabled = true;
            }
        }
    }

    private static Task OpenEspressifBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "mcp.espressif.com" || !uri.IsDefaultPort ||
            uri.AbsolutePath != "/docs/authorize" || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("乐鑫授权地址无效。");
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async void DisconnectEspressif_Click(object sender, RoutedEventArgs e)
    {
        if (espressifDocumentation is null || espressifConnectionCancellation is not null)
        {
            return;
        }
        DisconnectEspressifButton.IsEnabled = false;
        try
        {
            await espressifDocumentation.DisconnectAsync();
        }
        catch (Exception ex)
        {
            if (!espressifSettingsClosed)
            {
                ErrorText.Text = $"清除乐鑫授权失败（{ex.GetType().Name}）。";
            }
        }
        finally
        {
            if (!espressifSettingsClosed)
            {
                RefreshEspressifState();
            }
        }
    }

    private void CancelEspressif_Click(object sender, RoutedEventArgs e)
    {
        espressifConnectionCancellation?.Cancel();
        CancelEspressifButton.IsEnabled = false;
        EspressifState.Text = "正在取消连接…";
    }

    private void EspressifSettings_Closed(object? sender, EventArgs e)
    {
        // 使旧窗口回调失效，再取消在途授权；令牌源由连接处理器释放。
        espressifSettingsClosed = true;
        espressifConnectionCancellation?.Cancel();
    }
}
