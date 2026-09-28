namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>隔离的 Python 编辑器验收；只操作内存文档，不需要解释器或工程工具链。</summary>
    public async Task RenderPythonPreviewAsync(string directory)
    {
        var results = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            results.Add("PASS: " + message);
        }
        void SetSource(string text, int? caret = null, string path = "scripts/预处理.py")
        {
            ShowSource(new SourceDocument(path, text, Encoding.UTF8, "preview-only", false));
            SourceEditor.CaretOffset = caret ?? text.Length;
        }
        async Task Complete(string text, string expected, string name, int? caret = null)
        {
            SetSource(text, caret);
            QueueAssistance(signature: false, manual: true);
            await assistTask;
            var popup = completionWindow ?? throw new InvalidOperationException("Python 补全未显示：" + Status.Text);
            var item = popup.CompletionList.CompletionData.FirstOrDefault(item => item.Text == expected);
            Check(item is not null, "补全包含 " + expected);
            popup.CompletionList.ListBox.SelectedItem = item;
            UpdateLayout();
            popup.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(popup, Path.Combine(directory, name + ".png"));
            var before = SourceEditor.Text;
            popup.CompletionList.HandleKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(SourceEditor.Text != before && SourceEditor.Text.Contains(expected, StringComparison.Ordinal), "Tab 插入 " + expected);
            Check(!SourceEditor.Text.Contains("ntnt", StringComparison.Ordinal), "词中补全不重复后缀");
            SourceEditor.Undo();
            Check(SourceEditor.Text == before, "补全单步撤销 " + expected);
            CloseCodeAssistance();
        }
        renderingAssistancePreview = true;
        try
        {
            Check(!services.Intelligence.IsReady, "无 clangd 会话时仍可验证 Python");
            ApplyTheme(ThemeService.Dark);
            await Complete("print", "print", "builtin-dark", 3);
            await Complete("采样值 = 123\n采", "采样值", "identifier-dark");
            const string declaration = "def scale_sample(value, factor=2):\n    return value * factor\n\n";
            await Complete(declaration + "scale_s", "scale_sample", "function-dark");
            await Complete("ret", "return", "keyword-dark");
            SetSource("# 通用 Python 3 编辑示例\n" + declaration + "scale_sample(42, ");
            QueueAssistance(signature: true, manual: true);
            await assistTask;
            Check(signatureWindow is not null, "自定义函数参数弹窗");
            signatureWindow!.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(signatureWindow, Path.Combine(directory, "parameters-dark.png"));
            CloseCodeAssistance();
            SetSource("");
            foreach (var character in "pri")
            {
                SourceEditor.TextArea.PerformTextInput(character.ToString());
            }
            await assistTask;
            Check(completionWindow?.CompletionList.CompletionData.Any(item => item.Text == "print") == true, "连续键入自动补全");
            Code_PreviewKeyDown(SourceEditor.TextArea, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Escape));
            Check(completionWindow is null, "Esc 关闭提示");
            foreach (var text in new[] { "# pri", "value = '''多行\npri", "value = f\"{pri" })
            {
                SetSource(text);
                QueueAssistance(signature: false, manual: true);
                await assistTask;
                Check(completionWindow is null && signatureWindow is null, "注释或字符串内不弹出提示");
            }
            SetSource("pri");
            QueueAssistance(signature: false);
            var pending = assistTask;
            SourceEditor.Document.Insert(SourceEditor.CaretOffset, "x");
            await pending;
            Check(completionWindow is null, "编辑后取消过期请求");
            SetSource("pri");
            QueueAssistance(signature: false);
            pending = assistTask;
            SetSource("普通文本", path: "notes.txt");
            await pending;
            Check(completionWindow is null && !CanAssist, "切换文件取消旧 Python 补全");
            SetSource("    print(1)\n    print(2)\n");
            SourceEditor.Select(0, SourceEditor.Text.Length);
            ToggleSourceComment();
            Check(SourceEditor.Text == "    # print(1)\n    # print(2)\n", "批量 # 注释保留缩进");
            ToggleSourceComment();
            Check(SourceEditor.Text == "    print(1)\n    print(2)\n", "取消注释还原文本");
            SetSource("# Python 3 · 离线编辑支持\n@staticmethod\n" + declaration + "message = f\"采样值: {scale_sample(21)}\"\nprint(message)\n");
            Check(EditorLanguage.Text.StartsWith("Python", StringComparison.Ordinal), "状态栏识别 Python");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(SourceEditor, Path.Combine(directory, "editor-dark.png"));
            ApplyTheme(ThemeService.Light);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(SourceEditor, Path.Combine(directory, "editor-light.png"));
            await Complete("pri", "print", "builtin-light");
            await File.WriteAllLinesAsync(Path.Combine(directory, "result.txt"), results);
        }
        finally
        {
            CloseCodeAssistance();
            await assistTask;
            renderingAssistancePreview = false;
            foreach (var session in editorDocuments.ToArray())
            {
                RemoveEditor(session);
            }
        }
    }
}
