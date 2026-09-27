using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using StudioX.Desktop;

/// <summary>
/// 强制 WPF 创建和渲染编辑行，验证高亮异常只影响当前着色器，不破坏文档或后续编辑。
/// </summary>
internal static class SafeTextEditorChecks
{
    public static void Run(Action<bool, string> check)
    {
        var editor = new SafeTextEditor();
        var diagnostics = new List<(string Language, Exception Error)>();
        editor.HighlightingFailed += (language, error) => diagnostics.Add((language, error));
        var window = new Window
        {
            Content = editor,
            Width = 600,
            Height = 300,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        const string original = "SYSCLK 200\nBUSCLK 100\nHSECLK 8\nGPIO4_4 PIN_21\n";
        var document = new TextDocument(original);
        document.Insert(document.TextLength, "# 用户正在编辑的备注\n");
        var edited = document.Text;
        editor.Document = document;
        editor.SyntaxHighlighting = BrokenDefinition();
        try
        {
            window.Show();
            Render(window);
            FlushDispatcher();
            check(editor.SyntaxHighlighting is null && diagnostics.Count == 1,
                "真实 WPF 渲染中的坏高亮降级为纯文本，且只报告一次");
            check(diagnostics[0].Language == "Invalid VE regression" &&
                diagnostics[0].Error is InvalidOperationException &&
                diagnostics[0].Error.Message.Contains("matched 0 characters", StringComparison.Ordinal),
                "高亮失败回调保留语言名称与原始零长度匹配异常");
            check(ReferenceEquals(editor.Document, document) && document.Text == edited && document.UndoStack.CanUndo,
                "高亮失败保留当前编辑文档、未保存文本和撤销历史");
            document.UndoStack.Undo();
            check(document.Text == original, "降级后仍能撤销用户编辑");
            document.UndoStack.Redo();
            Render(window);
            FlushDispatcher();
            check(document.Text == edited && diagnostics.Count == 1,
                "降级后可重做并继续渲染，不重复报告同一次高亮异常");

            var normalDefinition = CodeLanguage.Get("AGM Pin Map", dark: true)!;
            editor.SyntaxHighlighting = normalDefinition;
            Render(window);
            FlushDispatcher();
            check(ReferenceEquals(editor.SyntaxHighlighting, normalDefinition) && diagnostics.Count == 1,
                "高亮降级后可恢复正常定义并继续渲染");

            // 延后的错误通知可能晚于用户切换文件；先触发失败，再切换文档，最后处理通知。
            editor.SyntaxHighlighting = BrokenDefinition();
            editor.TextArea.TextView.Redraw();
            Render(window);
            var nextDocument = new TextDocument("SYSCLK 144\nPIN_2:OUTPUT next_pin\n");
            editor.Document = nextDocument;
            editor.SyntaxHighlighting = normalDefinition;
            FlushDispatcher();
            Render(window);
            FlushDispatcher();
            check(ReferenceEquals(editor.Document, nextDocument) &&
                ReferenceEquals(editor.SyntaxHighlighting, normalDefinition) && diagnostics.Count == 1,
                "旧文档排队的高亮失败不能关闭新文档的正常高亮或报告过期错误");
        }
        finally
        {
            window.Close();
            FlushDispatcher();
        }
    }

    private static IHighlightingDefinition BrokenDefinition()
    {
        // 未转义 # 在 AvalonEdit 的正则空白模式下变成注释，匹配为空，复现原故障。
        const string xshd = """
            <SyntaxDefinition name="Invalid VE regression" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#7D8C79" />
              <RuleSet>
                <Span color="Comment"><Begin>#</Begin><End>$</End></Span>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var text = new StringReader(xshd);
        using var reader = XmlReader.Create(text);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
    }

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
