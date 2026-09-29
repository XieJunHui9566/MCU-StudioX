namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>可取消的统一检索窗口；关闭后不再接收迟到结果。</summary>
internal sealed class QuickPickWindow : Window
{
    internal TextBox Query { get; } = new() { Margin = new(0, 0, 0, 10), MinHeight = 32 };
    internal ListBox Results { get; } = new() { MinHeight = 260 };
    private readonly TextBlock status = new() { Margin = new(0, 7, 0, 0) };
    private CancellationTokenSource? cancellation;
    private bool finished;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<QuickPickItem>>> search;
    public QuickPickItem? Selected => Results.SelectedItem as QuickPickItem;
    public QuickPickWindow(string title, Func<string, CancellationToken, Task<IReadOnlyList<QuickPickItem>>> search)
    {
        this.search = search;
        Title = title;
        Width = 820;
        Height = 470;
        MinWidth = 550;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        Query.SetResourceReference(BackgroundProperty, "EditorSurface");
        Query.SetResourceReference(ForegroundProperty, "Text");
        Results.SetResourceReference(BackgroundProperty, "ToolSurface");
        Results.SetResourceReference(ForegroundProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(Query, title + " 搜索");
        System.Windows.Automation.AutomationProperties.SetName(Results, title + " 结果");
        var grid = new Grid { Margin = new(18) };
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new());
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Grid.SetRow(Results, 1);
        Grid.SetRow(status, 2);
        grid.Children.Add(Query);
        grid.Children.Add(Results);
        grid.Children.Add(status);
        Content = grid;
        Query.TextChanged += async (_, _) => await RefreshAsync();
        Results.MouseDoubleClick += (_, _) => Accept();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
            else if (Query.IsKeyboardFocusWithin && e.Key is Key.Down or Key.Up)
            {
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, Results.Items.Count - 1));
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
            }
        };
        Loaded += async (_, _) => { Query.Focus(); await RefreshAsync(); };
        Closed += (_, _) => { finished = true; cancellation?.Cancel(); };
    }
    private void Accept()
    {
        if (Selected is not null)
        {
            DialogResult = true;
        }
    }
    private async Task RefreshAsync()
    {
        cancellation?.Cancel();
        var current = new CancellationTokenSource();
        cancellation = current;
        try
        {
            status.Text = "正在查找…";
            Results.ItemsSource = null;
            await Task.Delay(120, current.Token);
            var items = await search(Query.Text, current.Token);
            if (finished || current.IsCancellationRequested)
            {
                return;
            }
            Results.ItemsSource = items;
            Results.SelectedIndex = items.Count > 0 ? 0 : -1;
            status.Text = items.Count == 0 ? "没有匹配项" : $"{items.Count} 项 · Enter 打开 · Esc 取消";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex) { if (!finished && !current.IsCancellationRequested) { status.Text = ex.Message; status.ToolTip = ex.ToString(); } }
        finally { if (cancellation == current) { cancellation = null; } current.Dispose(); }
    }
}
