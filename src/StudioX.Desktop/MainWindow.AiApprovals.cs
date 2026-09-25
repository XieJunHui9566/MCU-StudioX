namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StudioX.Application.Mcp;

public partial class MainWindow
{
    /// <summary>授权时显示核对卡，作出选择后移除；外部目录只读授权随本 MCP 会话结束。</summary>
    private async Task<bool> RequestAiMcpApprovalAsync(
        StudioXMcpApprovalRequest request, int generation, CancellationToken token)
    {
        // DesktopMcpAuthorizer 在桌面线程调用此方法，回调返回前不会执行工具动作。
        if (token.IsCancellationRequested || closing || closed || generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase))
            return false;

        if (AiSidebar.Visibility != Visibility.Visible) await ShowAiSidebarAsync();
        if (token.IsCancellationRequested || closing || closed || generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase))
            return false;

        var externalRead = request.Permission == StudioXMcpPermission.ExternalRead;
        var title = request.Permission switch
        {
            StudioXMcpPermission.ExternalRead => "读取外部示例目录",
            StudioXMcpPermission.FileWrite => "修改工程文件",
            StudioXMcpPermission.Build => "编译或配置工程",
            StudioXMcpPermission.FirmwareDownload => "烧录固件",
            StudioXMcpPermission.GitWrite => "Git 本地操作",
            StudioXMcpPermission.GitRemote => "Git 远端操作",
            StudioXMcpPermission.DebugControl => "控制调试会话",
            StudioXMcpPermission.HardwareConnect => "连接调试硬件",
            StudioXMcpPermission.SerialConnect => "连接串口",
            StudioXMcpPermission.SerialSend => "发送串口数据",
            StudioXMcpPermission.PlotConnect => "开始串口绘图",
            _ => "执行工程工具"
        };
        var detailsTooLong = request.Summary.Length > 1_600 ||
            request.Project.Length > 350 || request.Tool.Length > 100;
        var summary = BoundAiApprovalText(request.Summary, 1_600);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
        card.SetResourceReference(Border.BackgroundProperty, "ChromeSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var body = new StackPanel();
        var heading = new TextBlock
        {
            Text = $"需要授权 · {title}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(heading);
        body.Children.Add(AiApprovalDetail($"工具  {BoundAiApprovalText(request.Tool, 100)}", 6));
        body.Children.Add(AiApprovalDetail($"工程  {BoundAiApprovalText(request.Project, 350)}", 4));
        body.Children.Add(new ScrollViewer
        {
            Content = AiApprovalDetail(summary, 8),
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        if (detailsTooLong)
        {
            var warning = AiApprovalDetail("操作信息过长，无法完整核对；本次只能拒绝。", 6);
            warning.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            body.Children.Add(warning);
        }

        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var deny = new Button { Content = "拒绝", MinWidth = 68, Padding = new Thickness(12, 5, 12, 5) };
        AutomationProperties.SetName(deny, externalRead
            ? $"拒绝本会话读取外部目录的 {request.Tool} 授权"
            : $"拒绝本次 {request.Tool} 操作");
        deny.Click += (_, _) => choice.TrySetResult(false);
        actions.Children.Add(deny);
        var allow = new Button
        {
            Content = externalRead ? "允许本会话读取" : "允许本次",
            MinWidth = externalRead ? 120 : 88, Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5), IsEnabled = !detailsTooLong
        };
        allow.Style = (Style)FindResource("PrimaryButton");
        AutomationProperties.SetName(allow, externalRead
            ? $"允许本 MCP 会话通过 {request.Tool} 读取所列外部目录"
            : $"仅允许本次 {request.Tool} 操作");
        allow.Click += (_, _) =>
        {
            if (!token.IsCancellationRequested && !closing && !closed &&
                generation == aiProjectGeneration &&
                string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase))
                choice.TrySetResult(true);
        };
        actions.Children.Add(allow);
        body.Children.Add(actions);
        card.Child = body;
        AiTranscriptItems.Children.Add(card);
        UpdateAiEmptyHint();
        AiTranscript.ScrollToEnd();
        SetAiActivityStatus($"等待你确认 {request.Tool}…");
        AiStatus.Text = $"模型请求{title}，请在对话中核对并选择。";
        deny.Focus(); // 默认键盘焦点给拒绝按钮；Enter 不会意外批准。

        using var cancellation = token.Register(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetResult(false), choice);
        var requestedAllow = await choice.Task;
        var approved = requestedAllow && !token.IsCancellationRequested && !closing && !closed &&
            generation == aiProjectGeneration &&
            string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase);
        var outcomeText = closing || closed ? "已拒绝 · 窗口正在关闭" :
            generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase)
                ? "已拒绝 · 工程已切换" :
            token.IsCancellationRequested ? "已拒绝 · AI 请求已停止" :
            approved ? (externalRead ? "已允许本会话读取所列外部目录" : "已允许本次操作")
                : (externalRead ? "已拒绝读取外部目录" : "已拒绝本次操作");
        // 授权明细只在作决定前占据聊天区域；结果由活动状态和底部状态栏显示。
        AiTranscriptItems.Children.Remove(card);
        if (!closing && !closed && generation == aiProjectGeneration &&
            string.Equals(projectDirectory, request.Project, StringComparison.OrdinalIgnoreCase))
        {
            SetAiActivityStatus(approved ? $"已授权，正在执行 {request.Tool}…" :
                $"{request.Tool} 未获授权");
            AiStatus.Text = outcomeText;
            UpdateAiEmptyHint();
            AiTranscript.ScrollToEnd();
        }
        return approved;
    }

    private static TextBlock AiApprovalDetail(string text, double topMargin)
    {
        var detail = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, topMargin, 0, 0)
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return detail;
    }

    private static string BoundAiApprovalText(string text, int maximum)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum] + "…[已截断]";
    }
}
