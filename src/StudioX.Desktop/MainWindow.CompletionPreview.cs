namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>隔离的内存文档检查，不写入用户源文件、不编译或下载固件。</summary>
    public async Task RenderCompletionPreviewAsync(string directory, string project)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        if (!services.Intelligence.IsReady) throw new InvalidOperationException(Status.Text);
        var original = activeDocument!;
        renderingAssistancePreview = true;
        try
        {
            const string preamble = "#include <stdint.h>\n#include \"system.h\"\n\nvolatile uint32_t app_heartbeat = 0;\n\nint main(void)\n{\n    ";
            async Task Complete(string expression, string expected, string name)
            {
                ShowSource(original with { Text = preamble + expression + "\n}\n" }); SourceEditor.CaretOffset = preamble.Length + expression.Length;
                QueueAssistance(signature: false, manual: true); await assistTask;
                var popup = completionWindow ?? throw new InvalidOperationException("没有补全弹窗：" + Status.Text);
                if (!popup.CompletionList.CompletionData.Any(item => item.Text == expected)) throw new InvalidOperationException("缺少提示：" + expected);
                popup.CompletionList.ListBox.SelectedItem = popup.CompletionList.CompletionData.First(item => item.Text == expected); UpdateLayout(); popup.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(popup, Path.Combine(directory, name + ".png"));
                var before = SourceEditor.Text;
                popup.CompletionList.HandleKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
                if (!SourceEditor.Text.Contains(expected, StringComparison.Ordinal) || SourceEditor.Text == before) throw new InvalidOperationException("Tab 补全未插入。");
                SourceEditor.Undo(); if (SourceEditor.Text != before) throw new InvalidOperationException("补全未作为单步撤销。");
                CloseCodeAssistance();
            }
            await Complete("SYS->", "CLK_CNTL", "members");
            await Complete("SYS_Get", "SYS_GetDeviceID", "functions");
            await Complete("app_", "app_heartbeat", "variables");
            await Complete("ret", "return", "keywords");
            ShowSource(original with { Text = preamble + "\n}\n" }); SourceEditor.CaretOffset = preamble.Length;
            foreach (var character in "SYS_Get") SourceEditor.TextArea.PerformTextInput(character.ToString());
            await assistTask;
            if (completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text == "SYS_GetDeviceID")) throw new InvalidOperationException("连续输入没有自动提示。");
            Code_PreviewKeyDown(SourceEditor.TextArea, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(SourceEditor), 0, Key.Escape));
            if (completionWindow is not null) throw new InvalidOperationException("Esc 未关闭提示。");
            ShowSource(original with { Text = "#include \"sys" }); SourceEditor.CaretOffset = SourceEditor.Text.Length;
            QueueAssistance(signature: false, manual: true); await assistTask;
            if (completionWindow is null || !completionWindow.CompletionList.CompletionData.Any(item => item.Text.Contains("system.h", StringComparison.Ordinal))) throw new InvalidOperationException("没有头文件路径提示。");
            CloseCodeAssistance();
            ShowSource(original with { Text = preamble + "SYS_GetDeviceID\n}\n" });
            SourceEditor.CaretOffset = preamble.Length + "SYS_Get".Length;
            QueueAssistance(signature: false, manual: true); await assistTask;
            var middle = completionWindow ?? throw new InvalidOperationException("词中补全未返回。");
            middle.CompletionList.SelectItem("SYS_GetDeviceID"); middle.CompletionList.RequestInsertion(EventArgs.Empty);
            if (!SourceEditor.Text.Contains("SYS_GetDeviceID()", StringComparison.Ordinal) || SourceEditor.Text.Contains("DeviceIDDeviceID", StringComparison.Ordinal)) throw new InvalidOperationException("词中补全重复了后缀。");
            CloseCodeAssistance();
            ShowSource(original with { Text = preamble + "SYS_EnableAPBClock(\n}\n" });
            SourceEditor.CaretOffset = preamble.Length + "SYS_EnableAPBClock(".Length;
            QueueAssistance(signature: true, manual: true); await assistTask;
            var signature = signatureWindow ?? throw new InvalidOperationException("缺少参数提示：" + Status.Text);
            signature.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(signature, Path.Combine(directory, "parameters.png")); CloseCodeAssistance();
            ShowSource(original with { Text = preamble + "/* SYS_Get */\n}\n" });
            SourceEditor.CaretOffset = preamble.Length + "/* SYS_Get".Length;
            QueueAssistance(signature: false, manual: true); await assistTask;
            if (completionWindow is not null) throw new InvalidOperationException("注释中不应弹出提示。");
            ShowSource(original with { Text = preamble + "SYS_Get\n}\n" }); SourceEditor.CaretOffset = preamble.Length + 7;
            QueueAssistance(signature: false, manual: true); var staleRequest = assistTask;
            SourceEditor.Document.Insert(SourceEditor.CaretOffset, "X"); await staleRequest;
            if (completionWindow is not null) throw new InvalidOperationException("过期补全结果不应展示。");
            ApplyTheme(ThemeService.Light); await Complete("SYS->", "CLK_CNTL", "members-light"); ApplyTheme(ThemeService.Dark);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: AG32 members/functions/unsaved variables/keywords/header paths, automatic typed completion, Esc/Tab, single-step undo, mid-word replacement, signature help, comment suppression and stale-request cancellation. Dark/light popups rendered. Source files unchanged.\n");
        }
        finally { CloseCodeAssistance(); await assistTask; renderingAssistancePreview = false; ShowSource(original); }
    }
}
