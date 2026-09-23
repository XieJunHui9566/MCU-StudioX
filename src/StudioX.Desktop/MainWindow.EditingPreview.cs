namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>实际 GCC 诊断及编辑操作回归，只改隔离副本，不连接硬件。</summary>
    public async Task RenderEditingPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixture)) throw new InvalidOperationException("需要新的预览目录。");
        foreach (var path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        var main = activeEditor!; var original = main.Buffer.Text;
        Check(!SourceCommands.Undo.CanExecute(null, SourceEditor), "新文档撤销按钮禁用");
        const string sample = "    int first;\r\n\tint second;\r\n\r\nint untouched;\r\n";
        main.Buffer.Text = sample; SourceEditor.Select(0, sample.IndexOf("int untouched", StringComparison.Ordinal));
        SourceCommands.ToggleComment.Execute(null, SourceEditor);
        Check(main.Buffer.Text == "    // int first;\r\n\t// int second;\r\n\r\nint untouched;\r\n", "缩进、空行和下一行行首选择边界");
        Check(SourceCommands.Undo.CanExecute(null, SourceEditor), "撤销按钮可用");
        SourceCommands.Undo.Execute(null, SourceEditor); Check(main.Buffer.Text == sample, "多行注释一次撤销");
        SourceCommands.Redo.Execute(null, SourceEditor); Check(main.Buffer.Text.Contains("// int first", StringComparison.Ordinal), "注释重做");
        SourceEditor.Select(0, main.Buffer.Text.IndexOf("int untouched", StringComparison.Ordinal)); ToggleSourceComment();
        Check(main.Buffer.Text == sample, "取消多行注释");
        main.Buffer.Text = "// existing\nint next;"; SourceEditor.SelectAll(); ToggleSourceComment(); ToggleSourceComment();
        Check(main.Buffer.Text == "// existing\nint next;", "混合注释往返无损");
        SourceEditor.IsReadOnly = true; var unchanged = main.Buffer.Text;
        ToggleSourceComment(); Check(main.Buffer.Text == unchanged && !SourceCommands.Undo.CanExecute(null, SourceEditor), "只读保护");
        SourceEditor.IsReadOnly = false; main.Buffer.Text = original;

        await OpenSourceAsync("CMakeLists.txt", CancellationToken.None);
        var cmake = activeEditor!; var cmakeOriginal = cmake.Buffer.Text;
        cmake.Buffer.Text = "set(A 1)\n  set(B 2)\n"; SourceEditor.SelectAll(); ToggleSourceComment();
        Check(cmake.Buffer.Text == "# set(A 1)\n  # set(B 2)\n", "CMake 使用 #");
        ShowDocument(main.Tab); Check(main.Buffer.Text == original, "切换文件隔离文本");
        ShowDocument(cmake.Tab); SourceCommands.Undo.Execute(null, SourceEditor);
        Check(cmake.Buffer.Text == "set(A 1)\n  set(B 2)\n", "切换文件保留撤销栈");
        cmake.Buffer.Text = cmakeOriginal; ShowDocument(main.Tab);

        var mainPath = Path.Combine(fixture, "src", "main.c");
        var synthetic = $"\u001b[31m{mainPath}:3:9: error: undeclared symbol\u001b[0m\n../src/main.c:4: warning: unused variable\nCMake Error at CMakeLists.txt:7 (bad_command):\n  Unknown CMake command.\n\n";
        var parsed = BuildDiagnostics.Parse(fixture, synthetic);
        Check(parsed.Count == 3 && parsed[0].RelativePath == "src/main.c" && parsed[1].IsWarning && parsed[2].Message.Contains("Unknown CMake command", StringComparison.Ordinal), "GCC/ANSI/相对路径/CMake 解析");
        Check(BuildDiagnostics.Parse(fixture, "C:/outside/missing.c:1:2: error: fail\nld: undefined reference").Count == 0, "不捏造无效或链接错误位置");
        var tabDocument = new TextDocument("\tmissing_value();\n");
        var marker = EditorDiagnosticRenderer.Locate(tabDocument, new("src/main.c", 1, 9, false, "missing"))!;
        Check(tabDocument.GetText(marker) == "missing_value", "制表符列定位");
        var wideDocument = new TextDocument("/* 中 */ missing_value();");
        marker = EditorDiagnosticRenderer.Locate(wideDocument, new("src/main.c", 1, 10, false, "missing"))!;
        Check(wideDocument.GetText(marker) == "missing_value", "中文显示列定位");
        Check(EditorDiagnosticRenderer.Locate(new TextDocument(""), new("src/main.c", 1, 80, false, "empty")) is { Length: 0 }, "空行 / 文件末尾定位");

        var header = Path.Combine(fixture, "src", "diagnostic_fixture.h");
        await File.WriteAllTextAsync(header, "#error STUDIOX_EXPECTED_HEADER_ERROR\n");
        main.Buffer.Text = "#include \"diagnostic_fixture.h\"\n" + original + "\nint diagnostics_probe(void) { return missing_test_variable; }\n";
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        Check(Status.Text.StartsWith("编译失败", StringComparison.Ordinal) && diagnosticRenderer!.Markers.Any(m => m.Diagnostic.Message.Contains("missing_test_variable", StringComparison.Ordinal)), "真实编译失败产生波浪线");
        SourceEditor.ScrollToLine(main.Buffer.LineCount - 1);
        SourceEditor.Select(main.Buffer.GetLineByNumber(main.Buffer.LineCount - 1).Offset, 0);
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "compiler-error.png"));
        await OpenSourceAsync("src/diagnostic_fixture.h", CancellationToken.None);
        Check(diagnosticRenderer!.Markers.Any(m => m.Diagnostic.Message.Contains("STUDIOX_EXPECTED_HEADER_ERROR", StringComparison.Ordinal)), "未打开的头文件错误 / 切换标签");
        ShowDocument(main.Tab); Check(diagnosticRenderer.Markers.Count > 0, "切回 main 保留诊断");
        main.Buffer.Insert(0, "// edited\n"); Check(diagnosticRenderer.Markers.Count == 0 && buildDiagnostics.Count == 0, "编辑后清除过期诊断");
        var staleRevision = diagnosticRevision; main.Buffer.Insert(0, "// newer edit\n");
        await PublishBuildDiagnosticsAsync(fixture, synthetic, staleRevision, CancellationToken.None);
        Check(buildDiagnostics.Count == 0, "构建期间编辑后丢弃晚到诊断");
        main.Buffer.Text = original;
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        Check(Status.Text == "编译成功，退出代码：0" && diagnosticRenderer.Markers.Count == 0, "修复后成功编译无旧波浪线");
        await PublishBuildDiagnosticsAsync(fixture, synthetic, diagnosticRevision, CancellationToken.None);
        Check(buildDiagnostics.Count > 0, "关闭前有诊断");
        await CloseProjectAsync(CancellationToken.None);
        Check(buildDiagnostics.Count == 0 && diagnosticRenderer.Markers.Count == 0, "关闭工程清除诊断");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: real GCC errors in main/header, path/ANSI/CMake parsing, tab/Unicode locations, file switching, stale results, successful rebuild and close; undo/redo, atomic line comments, indentation/selection boundaries, CMake #, mixed comments and read-only protection. No hardware access.\n");

        static void Check(bool pass, string description) { if (!pass) throw new InvalidOperationException(description); }
    }
}
