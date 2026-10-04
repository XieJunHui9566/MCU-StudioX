namespace StudioX.Desktop;

using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private sealed record TemplateInsertionTarget(EditorDocumentSession Session, TextDocument Document, ITextSourceVersion Version, string? Project, int Start, int Length, int ViewCaret, int ViewStart, int ViewLength);
    private TemplateInsertionTarget? CaptureTemplateTarget(int? start = null, int? length = null) => !CanEditSource || activeEditor is null ? null :
        new(activeEditor, SourceEditor.Document, SourceEditor.Document.Version, projectDirectory, start ?? SourceEditor.SelectionStart, length ?? SourceEditor.SelectionLength, SourceEditor.CaretOffset, SourceEditor.SelectionStart, SourceEditor.SelectionLength);

    private void InitializeCodeTemplates()
    {
        Add("代码模板…", "Ctrl+Alt+T", () => true, ShowCodeTemplatesAsync);
        Add("将选区保存为代码模板…", "", () => CanEditSource && SourceEditor.SelectionLength > 0, SaveCodeSelectionAsync);
        InputBindings.Add(new KeyBinding(new PluginUiCommand(ShowCodeTemplatesAsync, () => true), new KeyGesture(Key.T, ModifierKeys.Control | ModifierKeys.Alt)));
        void Add(string title, string shortcut, Func<bool> enabled, Func<Task> execute)
        {
            workbenchCommands.Add(new(title, shortcut, enabled, execute));
            EditorMenu.Items.Add(new MenuItem { Header = title, InputGestureText = shortcut, Command = new PluginUiCommand(execute, enabled) });
            sourceContextMenu?.Items.Add(new MenuItem { Header = title, InputGestureText = shortcut, Command = new PluginUiCommand(execute, enabled) });
        }
    }

    private async Task ShowCodeTemplatesAsync()
    {
        var target = CaptureTemplateTarget();
        var project = projectDirectory;
        CloseCodeAssistance();
        try
        {
            var library = await services.CodeTemplates.LoadAsync(project);
            if (closing || project != projectDirectory)
            {
                return;
            }
            var manager = new CodeTemplateWindow(services.CodeTemplates, project, library, target is null ? null : CodeLanguage.ForFile(target.Session.Source.RelativePath), target is not null, Log) { Owner = this };
            if (manager.ShowDialog() == true && manager.Selected is { } selected && target is not null)
            {
                InsertCodeTemplate(selected.Template, target);
            }
        }
        catch (Exception ex) { Status.Text = "代码模板：" + ex.Message; Log(ex.ToString()); }
    }

    private async Task SaveCodeSelectionAsync()
    {
        if (CaptureTemplateTarget() is not { Length: > 0 } target)
        {
            return;
        }
        if (target.Length > CodeTemplateService.MaximumBodyLength)
        {
            Status.Text = "选区超过 65,536 字符，请缩小后保存为模板。";
            return;
        }
        var body = CodeTemplateExpander.CaptureSelection(target.Document.Text, target.Start, target.Length);
        if (body.Length > CodeTemplateService.MaximumBodyLength)
        {
            Status.Text = "选区转义后超过模板正文上限，请缩小选区。";
            return;
        }
        var template = new CodeTemplate(Guid.NewGuid().ToString("N"), "", "", CodeLanguage.ForFile(target.Session.Source.RelativePath), "", body);
        CloseCodeAssistance();
        try
        {
            var library = await services.CodeTemplates.LoadAsync(target.Project);
            if (closing || target.Project != projectDirectory)
            {
                return;
            }
            var editor = new CodeTemplateEditorWindow(services.CodeTemplates, target.Project, library, template, CodeTemplateScope.User, false, Log) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                Status.Text = "已保存代码模板：" + editor.Saved!.Name;
            }
        }
        catch (Exception ex) { Status.Text = "保存代码模板：" + ex.Message; Log(ex.ToString()); }
    }

    private bool TemplateTargetCurrent(TemplateInsertionTarget target) => !closing && CanEditSource && projectDirectory == target.Project &&
        activeEditor == target.Session && SourceEditor.Document == target.Document && target.Document.Version == target.Version &&
        SourceEditor.CaretOffset == target.ViewCaret && SourceEditor.SelectionStart == target.ViewStart && SourceEditor.SelectionLength == target.ViewLength &&
        target.Start >= 0 && target.Length >= 0 && target.Start <= target.Document.TextLength - target.Length;

    private void InsertCodeTemplate(CodeTemplate template, TemplateInsertionTarget target)
    {
        CloseCodeAssistance();
        if (!TemplateTargetCurrent(target))
        {
            Status.Text = "文件或编辑状态已变化，请重新选择插入位置。";
            return;
        }
        try
        {
            var snapshot = target.Document.Text;
            var context = new CodeTemplateContext(CodeTemplateExpander.CaptureSelection(snapshot, target.Start, target.Length, escape: false), Path.GetFileName(target.Session.Source.RelativePath));
            CodeTemplateExpansion Expand(IReadOnlyDictionary<string, string> values) => CodeTemplateExpander.PrepareInsertion(CodeTemplateExpander.Expand(template.Body, values, context), snapshot, target.Start);
            CodeTemplateExpansion expansion;
            if (CodeTemplateExpander.Describe(template.Body).Count > 0)
            {
                var variables = new CodeTemplateVariablesWindow(template, Expand) { Owner = this };
                if (variables.ShowDialog() != true || variables.Expansion is null)
                {
                    return;
                }
                expansion = variables.Expansion;
            }
            else
            {
                expansion = Expand(new Dictionary<string, string>());
            }
            if (ApplyCodeTemplate(target, expansion))
            {
                Status.Text = "已插入代码模板：" + template.Name + " · Ctrl+Z 可撤销";
            }
        }
        catch (Exception ex) { Status.Text = "插入代码模板：" + ex.Message; Log(ex.ToString()); }
    }

    private bool ApplyCodeTemplate(TemplateInsertionTarget target, CodeTemplateExpansion expansion)
    {
        if (!TemplateTargetCurrent(target))
        {
            Status.Text = "文件已变化或变为只读，请重新选择插入位置。";
            return false;
        }
        using (target.Document.RunUpdate())
        {
            target.Document.Replace(target.Start, target.Length, expansion.Text);
            SourceEditor.CaretOffset = target.Start + expansion.CaretOffset;
            SourceEditor.Select(SourceEditor.CaretOffset, 0);
        }
        SourceEditor.Focus();
        return true;
    }
}
