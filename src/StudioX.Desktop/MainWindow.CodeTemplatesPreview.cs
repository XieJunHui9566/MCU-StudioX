namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>隔离用户数据与文本工程，不启动编译器、探针或语言服务器。</summary>
    public async Task RenderCodeTemplatesPreviewAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool passed, string label) { if (!passed) { throw new InvalidOperationException(label); } checks.Add(label); }
        projectDirectory = Path.Combine(output, "project"); Directory.CreateDirectory(projectDirectory);
        const string original = "void demo(void)\r\n{\r\n    call();\r\n}\r\n";
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "demo.c"), original);
        ShowSource(new("demo.c", original, Encoding.UTF8, "fixture", false));
        await Settle();
        var start = original.IndexOf("call();", StringComparison.Ordinal);
        SourceEditor.Select(start, "call();".Length);
        var target = CaptureTemplateTarget()!;
        var template = BuiltInCodeTemplates.All[0];
        var context = new CodeTemplateContext("call();", "demo.c");
        var expansion = CodeTemplateExpander.PrepareInsertion(CodeTemplateExpander.Expand(template.Body, new Dictionary<string, string> { ["condition"] = "ready != 0" }, context), original, start);
        Check(ApplyCodeTemplate(target, expansion) && SourceEditor.Text.Contains("if (ready != 0)\r\n    {\r\n        call();", StringComparison.Ordinal), "insertion replaces selection with current indentation and CRLF");
        Check(activeEditor!.IsDirty && await File.ReadAllTextAsync(Path.Combine(projectDirectory, "demo.c")) == original, "template insertion stays in unsaved buffer");
        SourceEditor.Undo(); Check(SourceEditor.Text == original, "one undo restores the entire pre-insertion text");
        SourceEditor.Redo(); Check(SourceEditor.Text.Contains("ready != 0", StringComparison.Ordinal), "redo restores template expansion");
        SourceEditor.Undo(); SourceEditor.Select(start, 7);
        var stale = CaptureTemplateTarget()!; SourceEditor.AppendText("// later edit\r\n");
        var later = SourceEditor.Text; Check(!ApplyCodeTemplate(stale, expansion) && SourceEditor.Text == later, "stale target does not overwrite subsequent edits");
        SourceEditor.Undo(); SourceEditor.Select(start, 7); var readOnlyTarget = CaptureTemplateTarget()!;
        SourceEditor.IsReadOnly = true; Check(!ApplyCodeTemplate(readOnlyTarget, expansion) && SourceEditor.Text == original, "read-only state blocks insertion"); SourceEditor.IsReadOnly = false;
        var movedCaret = CaptureTemplateTarget()!; SourceEditor.CaretOffset = 0;
        Check(!ApplyCodeTemplate(movedCaret, expansion) && SourceEditor.Text == original, "moving caret invalidates an old insertion target");
        var saved = new CodeTemplate(Guid.NewGuid().ToString("N"), "自定义日志", "sxlog", "C", "示例：个人模板，不连接设备", "log(${name:counter});${cursor}");
        var library = await services.CodeTemplates.LoadAsync(projectDirectory);
        await services.CodeTemplates.SaveAsync(projectDirectory, CodeTemplateScope.User, library.User.Revision, [saved]);
        library = await services.CodeTemplates.LoadAsync(projectDirectory);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var manager = new CodeTemplateWindow(services.CodeTemplates, projectDirectory, library, "C", true, Log) { Owner = this, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            manager.Show(); await Settle();
            Check(manager.Templates.Items.Count == 5 && manager.Preview.ActualWidth > 400, theme.Id + " template manager has language filter and readable preview");
            Render(manager, Path.Combine(output, "templates-" + theme.Id + ".png"));
            manager.Query.Text = "自定义"; Check(manager.Templates.Items.Count == 1 && manager.Selected?.Template == saved, "template search selects personal template in " + theme.Id);
            manager.Close();
            var variables = new CodeTemplateVariablesWindow(template, values => CodeTemplateExpander.PrepareInsertion(CodeTemplateExpander.Expand(template.Body, values, context), original, start)) { Owner = this, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            variables.Show(); await Settle(); variables.Fields["condition"].Text = "ready != 0"; await Settle();
            Check(variables.Expansion?.Text == expansion.Text && variables.Preview.Text.Contains("call();", StringComparison.Ordinal), theme.Id + " variable edits update insertion preview");
            Render(variables, Path.Combine(output, "variables-" + theme.Id + ".png")); variables.Close();
            var editor = new CodeTemplateEditorWindow(services.CodeTemplates, projectDirectory, library, saved, CodeTemplateScope.User, true, Log) { Owner = this, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            editor.Show(); await Settle(); Check(editor.BodyField.Text == saved.Body && editor.BodyField.ActualHeight > 250, theme.Id + " editor shows template body without losing dollars");
            Render(editor, Path.Combine(output, "edit-template-" + theme.Id + ".png")); editor.Close();
        }
        ShowSource(new("plain.txt", "sxlog", Encoding.UTF8, "fixture", false)); SourceEditor.CaretOffset = 5;
        QueueAssistance(false, manual: true); await assistTask;
        Check(completionWindow is null, "language-specific template does not appear in plain text");
        ShowSource(new("other.c", "sxlog", Encoding.UTF8, "fixture", false)); SourceEditor.CaretOffset = 5;
        QueueAssistance(false, manual: true); await assistTask;
        Check(!services.Intelligence.IsReady && completionWindow?.CompletionList.CompletionData.Any(c => c is CodeTemplateCompletionData && c.Text == "sxlog") == true, "Ctrl+Space shows templates without clangd or installed SDK");
        CloseCodeAssistance();
        library = await services.CodeTemplates.LoadAsync(projectDirectory);
        var plain = saved with { Id = Guid.NewGuid().ToString("N"), Name = "无参数模板", Shortcut = "sxplain", Body = "hello ${fileName}${cursor}" };
        await services.CodeTemplates.SaveAsync(projectDirectory, CodeTemplateScope.User, library.User.Revision, [saved, plain]);
        ShowSource(new("word.c", "sxplainTAIL", Encoding.UTF8, "fixture", false)); SourceEditor.CaretOffset = 7;
        QueueAssistance(false, manual: true); await assistTask;
        var completions = completionWindow ?? throw new InvalidOperationException("Template completion window missing.");
        completions.CompletionList.SelectItem("sxplain"); completions.CompletionList.RequestInsertion(EventArgs.Empty); await Settle();
        Check(SourceEditor.Text == "hello word.c" && SourceEditor.CaretOffset == "hello word.c".Length, "actual completion insertion replaces whole shortcut including identifier suffix");
        SourceEditor.Undo(); Check(SourceEditor.Text == "sxplainTAIL", "actual completion template inserts as one undo operation");
        ShowSource(new("comments.c", "// sxplain", Encoding.UTF8, "fixture", false)); SourceEditor.CaretOffset = SourceEditor.Text.Length;
        QueueAssistance(false, manual: true); await assistTask;
        Check(completionWindow is null, "template completion preserves comment suppression");
        Check(services.Help.Get("code-templates").Markdown.Contains("${cursor}", StringComparison.Ordinal) && services.Help.DiagnosticTopic("CODE_TEMPLATE_STALE") == "code-templates", "offline help explains syntax and directs template errors to the relevant article");
        var previousTabTarget = CaptureTemplateTarget()!;
        ShowSource(new("separate.c", "unchanged", Encoding.UTF8, "fixture", false));
        Check(!ApplyCodeTemplate(previousTabTarget, expansion) && SourceEditor.Text == "unchanged", "switching documents cannot insert into another tab");
        Check(workbenchCommands.Any(c => c.Title == "代码模板…") && sourceContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => i.Header?.ToString() == "将选区保存为代码模板…"), "menu, command palette and source context menu expose templates");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, hardware = false, checks });
        // 验收草稿不进入正常用户会话，也不弹出退出保存提示。
        foreach (var session in editorDocuments) { session.Source = session.Source with { Text = session.Buffer.Text }; }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout(); }
    }
}
