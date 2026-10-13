namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using StudioX.Foundation;

internal sealed partial class MainWindow
{
    private async Task LoadDocumentAsync(string relativePath, CancellationToken token)
    {
        var buffer = buffers.FirstOrDefault(item => item.Snapshot.Source.RelativePath.Equals(relativePath, HostPlatform.PathComparison));
        if (buffer is null)
        {
            buffer = new(await workspace.ReadAsync(relativePath, token));
            buffers.Add(buffer);
            RebuildTabs();
        }
        SelectBuffer(buffer);
    }

    private void SelectBuffer(EditorBuffer buffer)
    {
        RememberPosition();
        activeBuffer = buffer;
        loadingDocument = true;
        editor.Document = buffer.Document;
        editor.CaretOffset = Math.Clamp(buffer.CaretOffset, 0, buffer.Document.TextLength);
        editor.ScrollToVerticalOffset(buffer.VerticalOffset);
        editor.ScrollToHorizontalOffset(buffer.HorizontalOffset);
        loadingDocument = false;
        welcome.IsVisible = false;
        editor.IsVisible = true;
        ApplySyntax();
        status.Text = buffer.Snapshot.Source.ReadOnlyReason ?? buffer.Snapshot.Source.RelativePath;
        UpdateTabSelection();
        UpdateControls();
        editor.Focus();
        buffer.SelectButton.BringIntoView();
    }

    private void RememberPosition()
    {
        if (activeBuffer is not null)
        {
            activeBuffer.CaretOffset = editor.CaretOffset;
            activeBuffer.VerticalOffset = editor.VerticalOffset;
            activeBuffer.HorizontalOffset = editor.HorizontalOffset;
        }
    }

    private void ApplySyntax()
    {
        var extension = Path.GetExtension(document?.Source.RelativePath ?? "").ToLowerInvariant();
        editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(extension is ".c" or ".h" or ".cpp" or ".hpp" ? "C++"
            : extension == ".py" ? "Python" : extension == ".json" ? "JavaScript" : "XML");
        if (extension is not (".c" or ".h" or ".cpp" or ".hpp" or ".py" or ".json" or ".xml" or ".xaml"))
        {
            editor.SyntaxHighlighting = null;
        }
        if (editor.SyntaxHighlighting is { } highlighting)
        {
            var light = currentTheme.Id == "studiox.light";
            foreach (var color in highlighting.NamedHighlightingColors)
            {
                var name = color.Name.ToLowerInvariant();
                var foreground = name.Contains("comment", StringComparison.Ordinal) ? (light ? "#527A3E" : "#6AAB73")
                    : name.Contains("string", StringComparison.Ordinal) || name.Contains("char", StringComparison.Ordinal) ? (light ? "#067D17" : "#6AAB73")
                    : name.Contains("number", StringComparison.Ordinal) || name.Contains("digit", StringComparison.Ordinal) ? (light ? "#1750EB" : "#2AACB8")
                    : name.Contains("preprocessor", StringComparison.Ordinal) || name.Contains("directive", StringComparison.Ordinal) ? (light ? "#9E880D" : "#BBB529")
                    : name.Contains("keyword", StringComparison.Ordinal) || name.Contains("type", StringComparison.Ordinal) || name.Contains("visibility", StringComparison.Ordinal) ? (light ? "#0033B3" : "#CC7832")
                    : currentTheme.Colors["Text"];
                color.Foreground = new SimpleHighlightingBrush(Color.Parse(foreground));
            }
        }
    }

    private void RebuildTabs()
    {
        tabStrip.Children.Clear();
        var home = new Button { Content = "欢迎", Height = 34, Padding = new Thickness(14, 4), BorderThickness = new Thickness(0) };
        home.Click += (_, _) => ShowWelcome();
        tabStrip.Children.Add(home);
        foreach (var buffer in buffers)
        {
            if (buffer.Tab.Children.Count > 0)
            {
                tabStrip.Children.Add(buffer.Tab);
                continue;
            }
            var label = buffer.Label;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(FileIcon(buffer.Snapshot.Source.RelativePath));
            row.Children.Add(label);
            var close = new Button { Content = new WorkbenchIcon("close") { Width = 12, Height = 12 }, Height = 22, Width = 22, Padding = new Thickness(3), BorderThickness = new Thickness(0) };
            close.Click += (_, e) => { e.Handled = true; RunOperationAsync(token => CloseBufferAsync(buffer, token)); };
            var select = buffer.SelectButton;
            var wired = select.Content is not null;
            select.Content = row;
            select.Height = 34;
            select.Padding = new Thickness(12, 4, 4, 4);
            select.BorderThickness = new Thickness(0, 0, 0, 2);
            if (!wired)
            {
                select.Click += (_, _) => { if (!busy && !closing) { SelectBuffer(buffer); } };
                select.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(select).Properties.IsMiddleButtonPressed)
                    {
                        RunOperationAsync(token => CloseBufferAsync(buffer, token));
                        e.Handled = true;
                    }
                };
            }
            ToolTip.SetTip(select, buffer.Snapshot.Source.RelativePath);
            buffer.Tab.Children.Add(select);
            buffer.Tab.Children.Add(close);
            tabStrip.Children.Add(buffer.Tab);
        }
        UpdateTabSelection();
        UpdateControls();
    }

    private void UpdateTabSelection()
    {
        if (tabStrip.Children.FirstOrDefault() is Button home)
        {
            home.Background = ColorBrush(activeBuffer is null ? "Background" : "Surface");
            home.Foreground = ColorBrush("Text");
            home.BorderThickness = new Thickness(0, 0, 0, 2);
            home.BorderBrush = ColorBrush(activeBuffer is null ? "Accent" : "Surface");
        }
        foreach (var buffer in buffers)
        {
            buffer.SelectButton.Background = ColorBrush(buffer == activeBuffer ? "Background" : "Surface");
            buffer.SelectButton.BorderBrush = ColorBrush(buffer == activeBuffer ? "Accent" : "Surface");
            buffer.Label.Foreground = ColorBrush("Text");
        }
    }

    private void CycleDocument(int direction)
    {
        if (busy || closing || buffers.Count == 0)
        {
            return;
        }
        var current = activeBuffer is null ? -1 : buffers.IndexOf(activeBuffer);
        SelectBuffer(buffers[(current + direction + buffers.Count) % buffers.Count]);
    }

    private Task SaveDocumentAsync(CancellationToken token) => activeBuffer is { } buffer ? SaveBufferAsync(buffer, token) : Task.CompletedTask;

    private async Task SaveBufferAsync(EditorBuffer buffer, CancellationToken token)
    {
        if (!buffer.IsDirty)
        {
            return;
        }
        buffer.Snapshot = await workspace.SaveAsync(buffer.Snapshot, buffer.Document.Text, token);
        status.Text = "已保存：" + buffer.Snapshot.Source.RelativePath;
        UpdateControls();
    }

    private async Task SaveAllAsync(CancellationToken token)
    {
        foreach (var buffer in buffers.Where(item => item.IsDirty).ToArray())
        {
            await SaveBufferAsync(buffer, token);
        }
    }

    private async Task<bool> ConfirmBuffersAsync(IReadOnlyList<EditorBuffer> selected, CancellationToken token)
    {
        var modified = selected.Where(buffer => buffer.IsDirty).ToArray();
        if (modified.Length == 0)
        {
            return true;
        }
        var dialog = new Window { Title = "保存修改", Width = 480, Height = 250, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ColorBrush("Surface"), Foreground = ColorBrush("Text") };
        var save = new Button { Content = "全部保存" };
        var discard = new Button { Content = "放弃修改" };
        var cancel = new Button { Content = "取消" };
        save.Click += (_, _) => dialog.Close("save");
        discard.Click += (_, _) => dialog.Close("discard");
        cancel.Click += (_, _) => dialog.Close("cancel");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = "以下文件尚未保存：\n" + string.Join("\n", modified.Take(5).Select(buffer => buffer.Snapshot.Source.RelativePath)) + (modified.Length > 5 ? "\n…" : ""), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, discard, cancel } }
            }
        };
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close("cancel")));
        var choice = await dialog.ShowDialog<string?>(this);
        token.ThrowIfCancellationRequested();
        if (choice == "save")
        {
            // 全部保存成功之后才移除标签；磁盘冲突时保留所有内存文档与撤销记录。
            foreach (var buffer in modified)
            {
                await SaveBufferAsync(buffer, token);
            }
        }
        return choice is "save" or "discard";
    }

    private Task CloseActiveAsync(CancellationToken token) => activeBuffer is { } buffer ? CloseBufferAsync(buffer, token) : Task.CompletedTask;

    private async Task CloseBufferAsync(EditorBuffer buffer, CancellationToken token)
    {
        if (!await ConfirmBuffersAsync([buffer], token))
        {
            return;
        }
        var index = buffers.IndexOf(buffer);
        buffers.Remove(buffer);
        if (activeBuffer == buffer)
        {
            activeBuffer = null;
            if (buffers.Count > 0)
            {
                SelectBuffer(buffers[Math.Min(index, buffers.Count - 1)]);
            }
            else
            {
                ShowWelcome();
            }
        }
        RebuildTabs();
    }

    private void ClearDocuments()
    {
        activeBuffer = null;
        buffers.Clear();
        loadingDocument = true;
        editor.Document = new TextDocument();
        loadingDocument = false;
        RebuildTabs();
        ShowWelcome();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs arguments)
    {
        if (allowClose)
        {
            return;
        }
        arguments.Cancel = true;
        if (closing)
        {
            return;
        }
        closing = true;
        operationCancellation?.Cancel();
        UpdateControls();
        try
        {
            await operation;
            if (!await ConfirmBuffersAsync(buffers, CancellationToken.None))
            {
                return;
            }
            await workspace.DisposeAsync();
            allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            status.Text = "退出失败：" + error.Message;
            AppendLog(error.ToString());
        }
        finally
        {
            closing = false;
            UpdateControls();
        }
    }
}
