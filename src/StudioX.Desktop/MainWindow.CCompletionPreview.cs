namespace StudioX.Desktop;

using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    /// <summary>真实 clangd 与 WPF 补全插入检查，全部编辑保留在隔离的内存文档中。</summary>
    public async Task RenderCCompletionPreviewAsync(string output, string project)
    {
        var checks = new List<string>();
        void Check(bool passed, string label)
        {
            if (!passed)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        projectDirectory = project;
        await services.Intelligence.StartAsync(project);
        renderingAssistancePreview = true;
        const string preamble = "int helper(int value);\r\nvoid probe(void)\r\n{\r\n    ";
        void Set(string expression, int? caret = null)
        {
            CloseCodeAssistance();
            foreach (var session in SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().ToArray())
            {
                SourceEditor.TextArea.PopStackedInputHandler(session);
            }
            var text = preamble + expression + "\r\n}\r\n";
            ShowSource(new SourceDocument("src/main.c", text, Encoding.UTF8, "preview", false));
            SourceEditor.Text = text;
            SourceEditor.CaretOffset = preamble.Length + (caret ?? expression.Length);
            SourceEditor.Select(SourceEditor.CaretOffset, 0);
            SourceEditor.Document.UndoStack.ClearAll();
        }
        async Task Suggest(string expected, bool manual = true)
        {
            await services.Intelligence.DocumentSymbolsAsync("src/main.c", SourceEditor.Text);
            QueueAssistance(signature: false, manual: manual);
            await assistTask;
            Check(completionWindow is not null, "popup exists for " + expected);
            var popup = completionWindow!;
            var item = popup.CompletionList.CompletionData.First(entry => entry.Text == expected);
            popup.CompletionList.ListBox.SelectedItem = item;
        }
        void KeyPress(Key key)
        {
            SourceEditor.TextArea.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        void Insert()
        {
            var popup = completionWindow!;
            popup.CompletionList.HandleKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        async Task Capture(string name)
        {
            UpdateLayout();
            completionWindow?.UpdateLayout();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Render);
            if (completionWindow is { } popup)
            {
                Render(popup, Path.Combine(output, name + "-popup.png"));
            }
            Render(SourceEditor, Path.Combine(output, name + "-editor.png"));
        }
        try
        {
            foreach (var (prefix, expected) in new[] { ("wh", "while"), ("if", "if"), ("sw", "switch") })
            {
                Set("");
                await services.Intelligence.DocumentSymbolsAsync("src/main.c", SourceEditor.Text);
                foreach (var character in prefix)
                {
                    SourceEditor.TextArea.PerformTextInput(character.ToString());
                }
                await assistTask;
                Check(completionWindow?.CompletionList.CompletionData.Any(item => item.Text == expected) == true, "typing automatically offers " + expected);
                completionWindow!.CompletionList.SelectItem(expected);
                await Capture(expected);
                var before = SourceEditor.Text;
                Insert();
                Check(SourceEditor.SelectedText == "expression" && !SourceEditor.Text.Contains("${", StringComparison.Ordinal), expected + " selects condition without protocol markers");
                SourceEditor.TextArea.PerformTextInput("1");
                CloseCodeAssistance();
                KeyPress(Key.Tab);
                Check(SourceEditor.SelectionLength == 0 && SourceEditor.CaretOffset == SourceEditor.Text.IndexOf("\r\n    }", preamble.Length, StringComparison.Ordinal), expected + " Tab finishes inside indented body");
                Check(!SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().Any(), expected + " final Tab releases snippet navigation");
                SourceEditor.Undo();
                SourceEditor.Undo();
                Check(SourceEditor.Text == before, expected + " insertion and condition edit each undo once");
            }
            Set("fo");
            await Suggest("for");
            var original = SourceEditor.Text;
            Insert();
            Check(SourceEditor.SelectedText == "init-statement", "for initially selects initializer");
            KeyPress(Key.Tab);
            Check(SourceEditor.SelectedText == "condition", "for Tab advances to condition");
            SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().Single().Move(backwards: true);
            Check(SourceEditor.SelectedText == "init-statement", "backward navigation returns to initializer");
            KeyPress(Key.Escape);
            Check(!SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().Any(), "Esc ends snippet navigation");
            SourceEditor.Undo();
            Check(SourceEditor.Text == original, "whole for statement has one undo step");

            Set("while", 2);
            await Suggest("while");
            Insert();
            Check(!SourceEditor.Text.Contains("}ile", StringComparison.Ordinal) && SourceEditor.Text.Contains("while (expression)", StringComparison.Ordinal), "mid-word control completion replaces identifier suffix");
            SourceEditor.Undo();
            Check(!SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().Any(), "undo releases deleted placeholder anchors");

            Set("helper(42)", 3);
            await Suggest("helper");
            Insert();
            Check(SourceEditor.Text == preamble + "helper(42)\r\n}\r\n", "existing call parameters remain intact");

            Set("hel");
            await Suggest("helper");
            Insert();
            Check(SourceEditor.SelectedText == "int value", "function completion selects its parameter");
            var saved = SourceEditor.Document;
            SourceEditor.Document = new TextDocument("other file");
            Check(!SourceEditor.TextArea.StackedInputHandlers.OfType<CompletionSnippetSession>().Any(), "changing documents releases snippet session");
            SourceEditor.Document = saved;

            Set("bad");
            var excessive = CodeSnippet.Parse(string.Concat(Enumerable.Repeat("{\n", 2000)))!;
            var unchanged = SourceEditor.Text;
            ShowCompletions([new("bad", excessive.Text, "bad", "", "", 15, null, "bad") { Snippet = excessive }], unchanged, SourceEditor.CaretOffset);
            Insert();
            Check(SourceEditor.Text == unchanged && Status.Text.Contains("1,048,576", StringComparison.Ordinal), "excessive expansion keeps source intact and reports its limit");

            foreach (var expression in new[] { "/* if", "// while", "\"switch" })
            {
                Set(expression);
                QueueAssistance(signature: false, manual: true);
                await assistTask;
                Check(completionWindow is null, "comments and strings suppress completion: " + expression);
            }
            Set("if");
            QueueAssistance(signature: false);
            var pending = assistTask;
            SourceEditor.Document.Insert(SourceEditor.CaretOffset, "x");
            await pending;
            Check(completionWindow is null, "stale request cannot open a popup after editing");

            var custom = ThemeService.Dark with
            {
                Id = "preview.custom",
                Colors = new Dictionary<string, string>(ThemeService.Dark.Colors) { ["Accent"] = "#B8E986", ["OnAccent"] = "#163020" }
            };
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                SourceEditor.FontSize = 18;
                Set("sw");
                await Suggest("switch");
                await Capture("theme-" + theme.Id);
                Insert();
                await Capture("selection-" + theme.Id);
                Check(SourceEditor.SelectedText == "expression" && SourceEditor.FontSize == 18, theme.Id + " uses normal editor selection at larger font size");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                hardware = false,
                checks
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            CloseCodeAssistance();
            await assistTask;
            renderingAssistancePreview = false;
            foreach (var session in editorDocuments)
            {
                session.Source = session.Source with
                {
                    Text = session.Buffer.Text
                };
            }
        }
    }
}
