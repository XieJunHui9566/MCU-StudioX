namespace StudioX.Desktop;

using System.Collections.ObjectModel;
using System.Windows;
using StudioX.Application.Mcp;

public partial class AiSettingsWindow
{
    private AiMcpSettingsService? mcpSettings;
    private readonly ObservableCollection<AiMcpServer> mcpServers = [];
    private readonly Dictionary<string, (AiMcpServer Server, string Token, bool Clear)> mcpTokens = new(StringComparer.Ordinal);
    private readonly List<AiMcpServer> removedMcp = [];
    private bool mcpLoaded;

    private async Task LoadMcpAsync()
    {
        McpServersList.ItemsSource = mcpServers;
        McpPanel.IsEnabled = false;
        if (mcpSettings is null)
        {
            return;
        }
        try
        {
            foreach (var server in await mcpSettings.LoadAsync())
            {
                mcpServers.Add(server);
            }
            mcpLoaded = true;
            McpPanel.IsEnabled = true;
        }
        catch (Exception ex) { ErrorText.Text = "无法读取自定义 MCP 配置：" + ex.Message; }
    }

    private void McpAdd_Click(object sender, RoutedEventArgs e) => EditMcp(null);
    private void McpEdit_Click(object sender, RoutedEventArgs e)
    {
        if (McpServersList.SelectedItem is AiMcpServer server)
        {
            EditMcp(server);
        }
    }
    private void EditMcp(AiMcpServer? existing)
    {
        if (mcpSettings is null)
        {
            return;
        }
        var dialog = new AiMcpServerWindow(mcpSettings, existing) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Server is not { } server)
        {
            return;
        }
        if (existing is not null)
        {
            removedMcp.Add(existing);
            mcpServers[mcpServers.IndexOf(existing)] = server;
        }
        else
        {
            mcpServers.Add(server);
        }
        McpServersList.SelectedItem = server;
        // 保留本次尚未提交的密钥，直到主设置窗口保存；取消窗口不会留下孤立凭据。
        if (dialog.BearerToken.Length > 0 || dialog.ClearToken)
        {
            mcpTokens[server.Id] = (server, dialog.BearerToken, dialog.ClearToken);
        }
        else if (mcpTokens.TryGetValue(server.Id, out var pending) &&
            !AiMcpSettingsService.HasSameConnection(pending.Server, server))
        {
            mcpTokens.Remove(server.Id);
        }
    }
    private void McpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (McpServersList.SelectedItem is not AiMcpServer server)
        {
            return;
        }
        removedMcp.Add(server);
        mcpServers.Remove(server);
        mcpTokens.Remove(server.Id);
    }

    private async Task SaveMcpAsync()
    {
        if (mcpSettings is null || !mcpLoaded)
        {
            return;
        }
        var servers = mcpServers.ToArray();
        AiMcpSettingsService.Validate(servers);
        foreach (var pending in mcpTokens.Values)
        {
            if (pending.Clear)
            {
                mcpSettings.DeleteToken(pending.Server);
            }
            else if (pending.Token.Length > 0)
            {
                mcpSettings.SetToken(pending.Server, pending.Token);
            }
        }
        await mcpSettings.SaveAsync(servers);
        foreach (var old in removedMcp.Distinct())
        {
            if (!servers.Any(server => AiMcpSettingsService.HasSameConnection(server, old)))
            {
                mcpSettings.DeleteToken(old);
            }
        }
        mcpTokens.Clear();
    }
}
