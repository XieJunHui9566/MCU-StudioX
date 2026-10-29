namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Mcp;

internal sealed class AiMcpServerWindow : Window
{
    private readonly TextBox name = new();
    private readonly ComboBox transport = new() { ItemsSource = new[] { "http", "sse", "stdio" } };
    private readonly TextBox endpoint = new();
    private readonly TextBox command = new();
    private readonly TextBox arguments = new() { AcceptsReturn = true, Height = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly PasswordBox token = new();
    private readonly CheckBox enabled = new() { Content = "启用此服务", IsChecked = true };
    private readonly CheckBox clear = new() { Content = "删除此配置已保存的 Bearer Token" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly string id;
    private readonly CancellationTokenSource lifetime = new();
    public AiMcpServer? Server
    {
        get; private set;
    }
    public string BearerToken => token.Password.Trim();
    public bool ClearToken => clear.IsChecked == true;

    public AiMcpServerWindow(AiMcpSettingsService service, AiMcpServer? existing = null)
    {
        id = existing?.Id ?? Guid.NewGuid().ToString("N");
        Title = existing is null ? "添加自定义 MCP" : "编辑自定义 MCP";
        Width = 560;
        MaxHeight = 810;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        token.SetResourceReference(Control.BackgroundProperty, "EditorSurface");
        token.SetResourceReference(Control.ForegroundProperty, "Text");
        token.SetResourceReference(Control.BorderBrushProperty, "Border");
        clear.SetResourceReference(Control.ForegroundProperty, "Text");
        enabled.SetResourceReference(Control.ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(22) };
        void Field(string label, FrameworkElement input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 5) });
            panel.Children.Add(input);
        }
        panel.Children.Add(new TextBlock { Text = "自定义 MCP", FontSize = 22, FontWeight = FontWeights.SemiBold });
        Field("名称", name);
        Field("传输：http = Streamable HTTP；sse = 旧版 SSE；stdio = 本地程序", transport);
        Field("HTTP / SSE 地址（完整 MCP 端点）", endpoint);
        Field("stdio 可执行文件（.exe 绝对路径）", command);
        Field("stdio 参数（每行一个，不加 shell 引号）", arguments);
        Field("Bearer Token（可选；留空保留已保存凭据）", token);
        panel.Children.Add(clear);
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock
        {
            Text = "密钥存入 Windows 凭据管理器。启用服务只提供发现入口；Agent 连接与调用仍按当前权限模式授权。\nstdio 会执行所选程序；请自行确认来源。不会自动安装其他运行环境。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 10, 0, 0)
        });
        panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var test = new Button { Content = "测试连接", Margin = new(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) };
        var save = new Button { Content = "确定", IsDefault = true };
        actions.Children.Add(test);
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        name.Text = existing?.Name ?? "";
        endpoint.Text = existing?.Endpoint ?? "";
        command.Text = existing?.Command ?? "";
        arguments.Text = string.Join(Environment.NewLine, existing?.Arguments ?? []);
        transport.SelectedItem = existing?.Transport ?? "http";
        enabled.IsChecked = existing?.Enabled ?? true;
        void RefreshTransport()
        {
            var local = (string?)transport.SelectedItem == "stdio";
            command.IsEnabled = arguments.IsEnabled = local;
            endpoint.IsEnabled = token.IsEnabled = clear.IsEnabled = !local;
        }
        transport.SelectionChanged += (_, _) => RefreshTransport();
        RefreshTransport();
        AiMcpServer Read()
        {
            var server = new AiMcpServer(id, name.Text.Trim(), (string)transport.SelectedItem,
                endpoint.Text.Trim(), command.Text.Trim(), arguments.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries), enabled.IsChecked == true);
            AiMcpSettingsService.Validate([server]);
            var bearer = BearerToken;
            if (bearer.Length > 2560 || bearer.Any(c => c is < '!' or > '~'))
            {
                throw new ArgumentException("Bearer Token 格式无效。");
            }
            return server;
        }
        save.Click += (_, _) =>
        {
            try
            {
                Server = Read();
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = save.IsEnabled = false;
            try
            {
                var server = Read();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await using var client = await service.ConnectAsync(server, timeout.Token, ClearToken ? "" : BearerToken.Length > 0 ? BearerToken : null);
                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                if (!lifetime.IsCancellationRequested)
                {
                    status.Text = $"连接成功，共 {tools.Count} 个工具。";
                }
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) { status.Text = $"连接失败（{ex.GetType().Name}）；请检查地址、凭据、服务状态。"; } }
            finally { if (!lifetime.IsCancellationRequested) { test.IsEnabled = save.IsEnabled = true; } }
        };
        Closed += (_, _) => { lifetime.Cancel(); };
    }
}
