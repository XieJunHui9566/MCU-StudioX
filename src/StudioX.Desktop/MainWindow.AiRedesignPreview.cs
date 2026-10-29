namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    private async Task RenderAiRedesignPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        Check(AiComposerKeyPolicy.Resolve(Key.Return, ModifierKeys.None, false, false, false) == AiComposerKeyAction.Send, "Enter sends");
        Check(AiComposerKeyPolicy.Resolve(Key.Return, ModifierKeys.Shift, true, false, false) == AiComposerKeyAction.NewLine, "left Shift+Enter inserts newline");
        Check(AiComposerKeyPolicy.Resolve(Key.Return, ModifierKeys.Shift, false, false, false) == AiComposerKeyAction.Send, "right Shift is not the left Shift newline shortcut");
        Check(AiComposerKeyPolicy.Resolve(Key.ImeProcessed, ModifierKeys.None, false, false, false) == AiComposerKeyAction.Ignore &&
            AiComposerKeyPolicy.Resolve(Key.Return, ModifierKeys.None, false, true, false) == AiComposerKeyAction.Ignore, "IME confirmation does not send");
        Check(AiComposerKeyPolicy.Resolve(Key.Return, ModifierKeys.None, false, false, true) == AiComposerKeyAction.Consume, "held Enter does not send repeatedly");
        AiPromptInput.Text = "前选区后";
        AiPromptInput.Select(1, 2);
        InsertAiComposerNewLine();
        Check(AiPromptInput.Text == "前" + Environment.NewLine + "后" && AiPromptInput.CaretIndex == 3,
            "newline replaces selected text and restores caret");
        AiPromptInput.Clear();
        AiSidebarColumn.Width = new GridLength(400);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "ai-redesign-" + theme.Id + ".png"));
            var settings = CreateAiSettingsWindow(new AiSettings());
            settings.ShowActivated = false;
            settings.ShowInTaskbar = false;
            settings.WindowStartupLocation = WindowStartupLocation.Manual;
            settings.Left = -20000;
            settings.Show();
            settings.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(settings.McpPanel.IsEnabled, "production settings entry supplies the MCP service");
            Render(settings, Path.Combine(directory, "ai-settings-" + theme.Id + ".png"));
            settings.McpSettingsTab.IsSelected = true;
            settings.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(settings, Path.Combine(directory, "ai-settings-mcp-" + theme.Id + ".png"));
            settings.Close();
        }
        ApplyTheme(ThemeService.Dark);
        var skills = new AiSkillsWindow(services.AiSkills, services.DataDirectory)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000
        };
        skills.Show();
        skills.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(skills, Path.Combine(directory, "ai-skills.dark.png"));
        skills.Close();
        var mcp = new AiMcpServerWindow(services.AiMcp)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000
        };
        mcp.Show();
        mcp.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(mcp, Path.Combine(directory, "ai-mcp.dark.png"));
        mcp.Close();
        await File.WriteAllLinesAsync(Path.Combine(directory, "ai-redesign-checks.txt"), checks.Select(name => "PASS " + name));
    }
}
