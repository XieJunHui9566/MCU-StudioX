namespace StudioX.Desktop;

using System.Windows.Threading;

public partial class MainWindow
{
    private async Task ShowStcDownloadIssueAsync(string root, string message, bool focusPort, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closing || closed || projectDirectory != root)
        {
            return;
        }
        await ShowStcDownloadSettingsAsync(root, token);
        if (closing || closed || projectDirectory != root)
        {
            return;
        }
        // 等待页切换布局完成后定位；不重新加载器件页或覆盖任何未保存草稿。
        if (focusPort)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (closing || closed || projectDirectory != root || !StcDownloadTab.IsSelected)
                {
                    return;
                }
                var picker = IsMx251Project ? Mx251SerialPortPicker : StcPortPicker;
                picker.BringIntoView();
                picker.Focus();
            }, DispatcherPriority.Loaded, token);
        }
        if (closing || closed || projectDirectory != root)
        {
            return;
        }
        // 编辑型下拉框在获得焦点时会刷新脏状态，提示必须在这之后呈现。
        Status.Text = message;
        if (IsMx251Project)
        {
            Mx251IspStatus.Text = message;
        }
        else
        {
            StcIspStatus.Text = message;
        }
    }

    private async Task<bool> RequireStcSerialPortAsync(string root, string port, Func<Task<string[]>> listPorts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closing || closed || projectDirectory != root)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(port))
        {
            // 只列举端口，不打开串口；消失的旧端口不能触发编译、监控制作或下载。
            var ports = await listPorts().WaitAsync(token);
            if (closing || closed || projectDirectory != root)
            {
                return false;
            }
            if (ports.Contains(port, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
            var picker = IsMx251Project ? Mx251SerialPortPicker : StcPortPicker;
            var draft = picker.Text;
            picker.ItemsSource = ports;
            picker.Text = draft;
        }
        var message = string.IsNullOrWhiteSpace(port) ? "请先选择并保存开发板使用的 COM 串口，再次点击下载或调试。" :
            $"保存的串口 {port} 当前未连接；请连接开发板后刷新串口，或重新选择并保存下载串口。";
        await ShowStcDownloadIssueAsync(root, message, true, token);
        return false;
    }
}
