namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>使用隔离用户目录和内存副本检验交互，不运行 CMake 或写入工程文件。</summary>
    public async Task RenderCMakePreviewAsync(string directory, string project)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        await OpenSourceAsync("CMakeLists.txt", CancellationToken.None);
        // CMake 编辑不依赖 clangd 或工具链状态。
        await services.Intelligence.DisposeAsync();
        var original = activeDocument!;
        renderingAssistancePreview = true;
        try
        {
            const string declarations = "cmake_minimum_required(VERSION 3.24)\nproject(firmware LANGUAGES C CXX ASM)\nadd_executable(firmware src/main.c)\n\n";
            void Show(string marked, string path = "CMakeLists.txt")
            {
                var caret = marked.IndexOf('|');
                ShowSource(original with { RelativePath = path, Text = marked.Remove(caret, 1) }); SourceEditor.CaretOffset = caret;
            }
            async Task Complete(string marked, string expected, string after, string name)
            {
                Show(marked); QueueAssistance(signature: false, manual: true); await assistTask;
                var popup = completionWindow ?? throw new InvalidOperationException("缺少 CMake 补全：" + Status.Text);
                popup.CompletionList.ListBox.SelectedItem = popup.CompletionList.CompletionData.First(item => item.Text == expected);
                UpdateLayout(); popup.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(popup, Path.Combine(directory, name + ".png"));
                var before = SourceEditor.Text;
                popup.CompletionList.HandleKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
                if (SourceEditor.Text != after) throw new InvalidOperationException("补全结果错误：" + SourceEditor.Text);
                SourceEditor.Undo(); if (SourceEditor.Text != before) throw new InvalidOperationException("补全不能单步撤销。");
                CloseCodeAssistance();
            }
            await Complete(declarations + "target_inc|", "target_include_directories", declarations + "target_include_directories()", "commands");
            await Complete(declarations + "target_inc|lude_directories(firmware PRIVATE src)", "target_include_directories", declarations + "target_include_directories(firmware PRIVATE src)", "existing-parenthesis");
            await Complete(declarations + "target_sources(f|)", "firmware", declarations + "target_sources(firmware)", "targets");
            await Complete(declarations + "target_include_directories(firmware PR|)", "PRIVATE", declarations + "target_include_directories(firmware PRIVATE)", "scopes");
            await Complete(declarations + "target_sources(firmware PRIVATE \"src/ma|in.c\")", "main.c", declarations + "target_sources(firmware PRIVATE \"src/main.c\")", "paths");
            await Complete(declarations + "message(\"${CMAKE_CUR|RENT_SOURCE_DIR}\")", "CMAKE_CURRENT_SOURCE_DIR", declarations + "message(\"${CMAKE_CURRENT_SOURCE_DIR}\")", "variables");
            await Complete(declarations + "set(CMAKE_BUILD_TYPE Re|)", "Release", declarations + "set(CMAKE_BUILD_TYPE Release)", "values");
            Show(declarations + "|");
            foreach (var character in "target_inc") SourceEditor.TextArea.PerformTextInput(character.ToString());
            await assistTask;
            if (completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text == "target_include_directories")) throw new InvalidOperationException("命令未自动提示。");
            Code_PreviewKeyDown(SourceEditor.TextArea, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Escape));
            if (completionWindow is not null) throw new InvalidOperationException("Esc 未关闭。");
            Show(declarations + "target_so|"); QueueAssistance(signature: false, manual: true); await assistTask;
            completionWindow!.CompletionList.SelectItem("target_sources"); completionWindow.CompletionList.RequestInsertion(EventArgs.Empty);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); await assistTask;
            if (!SourceEditor.Text.EndsWith("target_sources()", StringComparison.Ordinal) || SourceEditor.CaretOffset != SourceEditor.Text.Length - 1 || completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text == "firmware")) throw new InvalidOperationException("命令补全后未进入括号并提示目标。");
            CloseCodeAssistance();
            Show(declarations + "target_sources(firmware|)"); SourceEditor.TextArea.PerformTextInput(" "); await assistTask;
            if (completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text == "PRIVATE")) throw new InvalidOperationException("空格后未自动提示参数。");
            CloseCodeAssistance();
            Show("set(CMAKE_C_ST|)", "cmake/toolchain.cmake"); QueueAssistance(signature: false, manual: true); await assistTask;
            if (completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text == "CMAKE_C_STANDARD")) throw new InvalidOperationException(".cmake 路由失败。");
            CloseCodeAssistance();
            Show(declarations + "target_include_directories(firmware PRIVATE |)"); QueueAssistance(signature: true, manual: true); await assistTask;
            var signature = signatureWindow ?? throw new InvalidOperationException("缺少 CMake 用法提示。");
            signature.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(signature, Path.Combine(directory, "usage.png")); CloseCodeAssistance();
            Show(declarations + "#[=[ target_inc| ]=]"); QueueAssistance(signature: false, manual: true); await assistTask;
            if (completionWindow is not null || signatureWindow is not null) throw new InvalidOperationException("注释中弹出了提示。");
            Show(declarations + "target_inc|"); QueueAssistance(signature: false); var stale = assistTask;
            SourceEditor.Document.Insert(SourceEditor.CaretOffset, "X"); await stale;
            if (completionWindow is not null) throw new InvalidOperationException("过期结果未丢弃。");
            ApplyTheme(ThemeService.Light);
            await Complete(declarations + "target_include_directories(firmware PR|)", "PRIVATE", declarations + "target_include_directories(firmware PRIVATE)", "scopes-light");
            ApplyTheme(ThemeService.Dark);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: CMake command/target/scope/variable/path/value completion, parentheses and brace reuse, Tab insertion and undo, auto command/space triggers, .cmake dispatch, usage help, comment suppression, stale cancellation, dark/light UI. clangd stopped during checks. Project files unchanged.\n");
        }
        finally { CloseCodeAssistance(); await assistTask; renderingAssistancePreview = false; ShowSource(original); }
    }
}
