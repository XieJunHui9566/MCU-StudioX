using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;
using StudioX.Desktop;
using StudioX.Foundation;

/// <summary>逐个独立子进程验证真实读取器、纯文本编辑器及文件图标；致命异常和卡住均由父进程记录。</summary>
internal static class UnknownFileChecks
{
    public static async Task<int> RunAsync(string output)
    {
        if (Directory.Exists(output)) { throw new IOException("Use a new evidence directory."); }
        var root = Path.Combine(output, "fixtures");
        Directory.CreateDirectory(root);
        var cases = new List<(string Path, string Expected)>();
        async Task Add(string path, byte[] bytes, string expected = "text")
        {
            await File.WriteAllBytesAsync(Path.Combine(root, path), bytes);
            cases.Add((path, expected));
        }
        const string sample = "未知后缀 · plain text\r\nint value = 1;\r\n中文 😀\n";
        foreach (var name in new[] { "notes.unknown", "main.c.xyz", "UPPER.UNRECOGNIZED", "Makefile", ".editorconfig", "中文.自定义" })
        {
            await Add(name, Encoding.UTF8.GetBytes(sample));
        }
        foreach (var (name, encoding) in new (string, Encoding)[] {
            ("utf8.odd", new UTF8Encoding(true, true)),
            ("utf16le.odd", new UnicodeEncoding(false, true, true)),
            ("utf16be.odd", new UnicodeEncoding(true, true, true)) })
        {
            await Add(name, encoding.GetPreamble().Concat(encoding.GetBytes(sample)).ToArray());
        }
        await Add("empty.odd", []);
        await Add("bom-only.odd", [0xef, 0xbb, 0xbf]);
        await Add("controls.odd", Encoding.UTF8.GetBytes("start\u0001\u0007\u000b\u000c\u001b\u007f\u202eend\u202c\n"));
        await Add("noncharacters.odd", Encoding.UTF8.GetBytes("a\ufffe\uffff\ufdd0b\n"));
        await Add("binary-nul.odd", [65, 0, 66], "EDITOR_BINARY");
        await Add("binary-nul.c", [65, 0, 66], "EDITOR_BINARY");
        await Add("executable.odd", [0x4d, 0x5a, 0, 0, 0x80], "EDITOR_ENCODING");
        await Add("bad-utf8.odd", [0xc0, 0xaf], "EDITOR_ENCODING");
        await Add("surrogate-utf8.odd", [0xed, 0xa0, 0x80], "EDITOR_ENCODING");
        await Add("short-utf16.odd", [0xff, 0xfe, 0x61], "EDITOR_ENCODING");
        await Add("surrogate-utf16.odd", [0xff, 0xfe, 0x00, 0xd8], "EDITOR_ENCODING");
        await Add("utf32.odd", [0xff, 0xfe, 0, 0, 0x41, 0, 0, 0], "EDITOR_BINARY");
        await Add("too-large.odd", Encoding.ASCII.GetBytes(new string('x', 4 * 1024 * 1024 + 1)), "EDITOR_FILE_SIZE");
        await Add("max-line.odd", Encoding.ASCII.GetBytes(new string('x', 4 * 1024 * 1024)), "EDITOR_LINE_LENGTH");
        await Add("line-boundary.odd", Encoding.ASCII.GetBytes(new string('x', EditorTextSafety.MaximumLineLength)));
        await Add("line-over-boundary.odd", Encoding.ASCII.GetBytes(new string('x', EditorTextSafety.MaximumLineLength + 1)), "EDITOR_LINE_LENGTH");
        await Add("many-lines.odd", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a\n", 100000))));
        await Add("combining.odd", Encoding.UTF8.GetBytes("a" + new string('\u0301', 100000)), "EDITOR_TEXT_COMPLEXITY");
        await Add("cluster-boundary.odd", Encoding.UTF8.GetBytes("a" + new string('\u0301', EditorTextSafety.MaximumTextElementLength - 1)));
        await Add("cluster-over-boundary.odd", Encoding.UTF8.GetBytes("a" + new string('\u0301', EditorTextSafety.MaximumTextElementLength)), "EDITOR_TEXT_COMPLEXITY");
        await Add("locked.odd", Encoding.UTF8.GetBytes(sample), "IO");
        cases.Add(("deleted.odd", "IO"));
        var results = new List<object>();
        var passed = true;
        foreach (var test in cases)
        {
            FileStream? held = test.Path == "locked.odd" ? File.Open(Path.Combine(root, test.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await new ProcessRunner().RunAsync(new(Environment.ProcessPath!,
                    ["--unknown-file-child", root, test.Path, test.Expected], output,
                    TimeSpan.FromSeconds(15), ContainProcessTree: true));
                var success = result.Success && !result.TimedOut;
                passed &= success;
                results.Add(new { path = test.Path, expected = test.Expected, passed = success, result.ExitCode, result.TimedOut, elapsedMs = stopwatch.ElapsedMilliseconds, result.StandardOutput, result.StandardError });
                Console.WriteLine($"{(success ? "PASS" : "FAIL")} {test.Path}: {test.Expected}, exit={result.ExitCode}, timeout={result.TimedOut}, {stopwatch.ElapsedMilliseconds} ms");
            }
            finally { held?.Dispose(); }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed, hardware = false, paidApi = false, cases = cases.Count, results });
        return passed ? 0 : 1;
    }

    public static int RunChild(string root, string path, string expected)
    {
        try
        {
            _ = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var files = new ProjectFileService();
            var editor = new SafeTextEditor { FontFamily = new FontFamily("Consolas"), ShowLineNumbers = true };
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            const string original = "previous document\n";
            editor.Document = new TextDocument(original);
            editor.Document.Insert(editor.Document.TextLength, "// unsaved draft\n");
            var previous = editor.Document;
            SourceDocument? source = null;
            string? rejected = null;
            try { source = Task.Run(() => files.ReadAsync(root, path)).GetAwaiter().GetResult(); }
            catch (StudioXException error) { rejected = error.Code; }
            catch (IOException) { rejected = "IO"; }
            if (expected == "text" ? source is null : rejected != expected)
            {
                throw new InvalidOperationException($"Expected {expected}, actual {rejected ?? "text"}.");
            }
            if (source is null && (!ReferenceEquals(editor.Document, previous) || !previous.Text.EndsWith("// unsaved draft\n", StringComparison.Ordinal) || !previous.UndoStack.CanUndo))
            {
                throw new InvalidOperationException("Rejected file changed the existing document or undo history.");
            }
            if (source is not null)
            {
                if (CodeLanguage.ForFile(path) != "Text" || CodeLanguage.Get("Text", true) is not null || CodeLanguage.Get("Text", false) is not null)
                {
                    throw new InvalidOperationException("Unknown suffix did not select plain text.");
                }
                var session = new EditorDocumentSession(source);
                editor.Document = session.Buffer;
                editor.SyntaxHighlighting = CodeLanguage.Get(CodeLanguage.ForFile(path), true);
            }
            var panel = new DockPanel();
            var icon = new FileIcon { FileName = path, Width = 24, Height = 24 };
            DockPanel.SetDock(icon, Dock.Top);
            panel.Children.Add(icon);
            panel.Children.Add(editor);
            var window = new Window { Content = panel, Width = 600, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                foreach (var dark in new[] { true, false })
                {
                    var theme = dark ? ThemeService.Dark : ThemeService.Light;
                    System.Windows.Application.Current.Resources["Background"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Colors["Background"]));
                    editor.Background = (Brush)System.Windows.Application.Current.Resources["Background"];
                    editor.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Colors["Text"]));
                    foreach (var size in new[] { 13d, 18d })
                    {
                        editor.FontSize = size;
                        icon.InvalidateVisual();
                        Render(window);
                    }
                }
                var before = editor.Text;
                editor.Document.Insert(editor.Document.TextLength, "\n// edit");
                editor.Undo();
                if (editor.Text != before) { throw new InvalidOperationException("Editing/undo changed the original text."); }
                editor.Redo();
                Render(window);
                editor.Document = new TextDocument("int main(void) { return 0; }\n");
                editor.SyntaxHighlighting = CodeLanguage.Get("C", true);
                Render(window);
                if (editor.Document.TextLength == 0) { throw new InvalidOperationException("Known-source switch failed."); }
                if (source is { Text.Length: < 4096 })
                {
                    var saved = Task.Run(() => files.SaveAsync(root, source, source.Text)).GetAwaiter().GetResult();
                    if (saved.DiskHash != source.DiskHash) { throw new InvalidOperationException("Plain-text save changed original encoding or bytes."); }
                }
                Console.WriteLine(JsonSerializer.Serialize(new { path, rejected, characters = source?.Text.Length, peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, passed = true }));
                return 0;
            }
            finally { window.Close(); }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
    }
}
