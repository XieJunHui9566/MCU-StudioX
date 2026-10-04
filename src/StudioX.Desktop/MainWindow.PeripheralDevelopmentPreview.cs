namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>验收参数及源码/依赖的未保存应用和整体撤销；不运行生成代码或建立硬件会话。</summary>
    public async Task RenderPeripheralDevelopmentPreviewAsync(string output, string referenceProject)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        projectDirectory = Path.Combine(output, "ui-project");
        foreach (var relative in new[] { ".studiox/project.json", "device/manifest.json" })
        {
            var destination = PathBoundary.Resolve(projectDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(PathBoundary.Resolve(referenceProject, relative), destination);
        }
        const string original = "// UI fixture\r\nvoid app_main(void)\r\n{\r\n}\r\n";
        var file = PathBoundary.Resolve(projectDirectory, "main/ui.c");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, original, Encoding.UTF8);
        const string originalCMake = "# user CMake notes\r\nidf_component_register(SRCS \"ui.c\" INCLUDE_DIRS \".\" PRIV_REQUIRES freertos esp_system)\r\n";
        var cmakeFile = PathBoundary.Resolve(projectDirectory, "main/CMakeLists.txt");
        await File.WriteAllTextAsync(cmakeFile, originalCMake, Encoding.UTF8);
        var context = await services.PeripheralDevelopment.ReadAsync(projectDirectory);
        ShowSource(await services.Files.ReadAsync(projectDirectory, "main/ui.c"));
        await Settle();
        var component = await services.PeripheralDevelopment.ReadComponentAsync(context, "main/ui.c", CaptureWorkspaceBuffers());
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var window = new PeripheralDevelopmentWindow(services.PeripheralDevelopment, context, true, Log, component) { Owner = this, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            window.Show();
            await Settle();
            Check(window.Options.Items.Count == 8 && window.PreviewResult is null && !window.Insert.IsEnabled, theme.Id + " initial blank board pin blocks insertion");
            window.Fields["pin"].Text = "1";
            await Settle();
            Check(window.PreviewResult?.Code.Contains("gpio_set_level", StringComparison.Ordinal) == true && window.Insert.IsEnabled, theme.Id + " valid GPIO refreshes preview");
            window.Fields["pin"].Text = "22";
            Check(window.PreviewResult is null && !window.Insert.IsEnabled, theme.Id + " out-of-range GPIO clears stale preview");
            window.Options.SelectedItem = context.Options.Single(o => o.Id == "i2c");
            window.Fields["sda"].Text = "0";
            window.Fields["scl"].Text = "0";
            Check(!window.Insert.IsEnabled && window.StatusLine.Text.Contains("不能重复", StringComparison.Ordinal), theme.Id + " conflicting pins cannot insert");
            window.Fields["scl"].Text = "1";
            await Settle();
            Check(window.PreviewResult?.Code.Contains("i2c_new_master_bus", StringComparison.Ordinal) == true && window.AdditionResult?.AddedComponents.Contains("esp_driver_i2c") == true, theme.Id + " I2C preview shows current API and automatic dependency plan");
            Check(!window.DependencyDetails.IsExpanded && window.Insert.Content.ToString() == "添加到工程" && window.StatusLine.Text.Contains("现有依赖保留", StringComparison.Ordinal), theme.Id + " default flow hides CMake details behind add-to-project action");
            Check(window.Preview.ActualWidth > 400 && window.Guidance.ActualHeight >= 190 && window.Fields.Values.All(box => box.ActualWidth > 150), theme.Id + " code and parameter controls remain readable");
            Render(window, Path.Combine(output, "peripheral-" + theme.Id + ".png"));
            window.Close();
        }
        var unavailable = context with
        {
            Options = context.Options.Select(o => o.Id == "uart" ? o with { Available = false, Availability = "SDK 缺少文件：driver/uart.h" } : o).ToArray()
        };
        var blocked = new PeripheralDevelopmentWindow(services.PeripheralDevelopment, unavailable, false, Log);
        blocked.Options.SelectedItem = unavailable.Options.Single(o => o.Id == "uart");
        Check(blocked.PreviewResult is null && !blocked.Insert.IsEnabled && blocked.Guidance.Text.Contains("缺少文件", StringComparison.Ordinal), "missing SDK header is displayed and blocks generation");
        blocked.Close();
        var target = CaptureTemplateTarget(start: 0, length: 0)!;
        var gpio = services.PeripheralDevelopment.Generate(context, "gpio", new Dictionary<string, string> { ["name"] = "preview", ["pin"] = "1", ["level"] = "0" });
        await services.PeripheralDevelopment.ValidateAsync(context);
        var code = services.PeripheralDevelopment.PrepareInsertion(gpio, target.Document.Text);
        var addition = services.PeripheralDevelopment.PrepareAddition(context, component, gpio);
        await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
        var cmakeEditor = FindEditor("main/CMakeLists.txt")!;
        Check(SourceEditor.Text.EndsWith(original, StringComparison.Ordinal) && cmakeEditor.Buffer.Text.Contains("esp_driver_gpio", StringComparison.Ordinal), "addition updates source and CMake together");
        Check(activeEditor!.IsDirty && cmakeEditor.IsDirty && await File.ReadAllTextAsync(file) == original && await File.ReadAllTextAsync(cmakeFile) == originalCMake,
            "source and dependency changes remain unsaved on disk");
        await UndoPeripheralAdditionAsync(CancellationToken.None);
        Check(SourceEditor.Text == original && cmakeEditor.Buffer.Text == originalCMake && !activeEditor.IsDirty && !cmakeEditor.IsDirty,
            "one explicit undo restores source and dependency baselines together");
        component = await services.PeripheralDevelopment.ReadComponentAsync(context, "main/ui.c", CaptureWorkspaceBuffers());
        addition = services.PeripheralDevelopment.PrepareAddition(context, component, gpio);
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
        cmakeEditor.Buffer.Insert(cmakeEditor.Buffer.TextLength, "# later CMake edit\r\n");
        var editedCMake = cmakeEditor.Buffer.Text;
        try
        {
            await UndoPeripheralAdditionAsync(CancellationToken.None);
            throw new InvalidOperationException("expected guarded undo");
        }
        catch (StudioXException ex) when (ex.Code == "PERIPHERAL_UNDO_STALE") { Check(cmakeEditor.Buffer.Text == editedCMake && SourceEditor.Text == addition.Files[0].After, "whole undo preserves later CMake edits and source"); }
        cmakeEditor.Buffer.UndoStack.Undo();
        await UndoPeripheralAdditionAsync(CancellationToken.None);
        cmakeEditor.Buffer.Insert(cmakeEditor.Buffer.TextLength, "# before addition unsaved\r\n");
        component = await services.PeripheralDevelopment.ReadComponentAsync(context, "main/ui.c", CaptureWorkspaceBuffers());
        addition = services.PeripheralDevelopment.PrepareAddition(context, component, gpio);
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
        Check(cmakeEditor.Buffer.Text.EndsWith("# before addition unsaved\r\n", StringComparison.Ordinal), "application preserves unsaved CMake notes");
        await UndoPeripheralAdditionAsync(CancellationToken.None);
        Check(cmakeEditor.Buffer.Text == component.CMake.Text && SourceEditor.Text == original, "whole undo preserves CMake edits made before addition");
        cmakeEditor.Buffer.Replace(0, cmakeEditor.Buffer.TextLength, originalCMake);
        component = await services.PeripheralDevelopment.ReadComponentAsync(context, "main/ui.c", CaptureWorkspaceBuffers());
        addition = services.PeripheralDevelopment.PrepareAddition(context, component, gpio);
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        cmakeEditor.Buffer.Insert(cmakeEditor.Buffer.TextLength, "# stale preview\r\n");
        try
        {
            await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
            throw new InvalidOperationException("expected stale CMake rejection");
        }
        catch (StudioXException ex) when (ex.Code == "PERIPHERAL_ADDITION_STALE") { Check(SourceEditor.Text == original && cmakeEditor.Buffer.Text.EndsWith("# stale preview\r\n", StringComparison.Ordinal), "stale dependency preview prevents partial source insertion"); }
        cmakeEditor.Buffer.UndoStack.Undo();
        component = await services.PeripheralDevelopment.ReadComponentAsync(context, "main/ui.c", CaptureWorkspaceBuffers());
        addition = services.PeripheralDevelopment.PrepareAddition(context, component, gpio);
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        EventHandler failEdit = (_, _) => throw new InvalidOperationException("injected CMake edit event failure");
        cmakeEditor.Buffer.TextChanged += failEdit;
        try
        {
            await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
            throw new InvalidOperationException("expected event failure");
        }
        catch (AggregateException ex)
        {
            Check(SourceEditor.Text == original && cmakeEditor.Buffer.Text == originalCMake &&
                ex.ToString().Contains("injected CMake edit event failure", StringComparison.Ordinal), "edit event failure rolls both buffers back and preserves raw diagnostics");
        }
        finally { cmakeEditor.Buffer.TextChanged -= failEdit; }
        var unsupported = new PeripheralDevelopmentWindow(services.PeripheralDevelopment, context, true, Log, null, "组件依赖使用了变量，请核对注册入口");
        unsupported.Fields["pin"].Text = "1";
        Check(unsupported.PreviewResult is not null && !unsupported.Insert.IsEnabled && unsupported.StatusLine.Text.Contains("变量", StringComparison.Ordinal), "unsupported CMake remains copyable but cannot partially apply code");
        unsupported.Close();
        Check(ApplyCodeTemplate(CaptureTemplateTarget(start: 0, length: 0)!, new CodeTemplateExpansion(code, code.Length)), "normal source undo remains available");
        SourceEditor.Undo();
        Check(SourceEditor.Text == original, "source undo restores source");
        SourceEditor.Redo();
        Check(SourceEditor.Text.StartsWith("// StudioX", StringComparison.Ordinal), "source redo restores generated code");
        SourceEditor.Undo();
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        SourceEditor.AppendText("// later edit\r\n");
        var later = SourceEditor.Text;
        Check(!ApplyCodeTemplate(target, new(code, code.Length)) && SourceEditor.Text == later, "stale preview cannot overwrite a later buffer revision");
        SourceEditor.Undo();
        target = CaptureTemplateTarget(start: 0, length: 0)!;
        SourceEditor.IsReadOnly = true;
        Check(!ApplyCodeTemplate(target, new(code, code.Length)) && SourceEditor.Text == original, "read-only buffer cannot accept generated code");
        SourceEditor.IsReadOnly = false;
        Check(workbenchCommands.Any(c => c.Title == "外设开发辅助…") && ProjectToolsMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => i.Header?.ToString() == "外设开发辅助…"), "tools menu and command palette expose assistance");
        Check(workbenchCommands.Any(c => c.Title == "撤销上次外设添加"), "command palette exposes grouped peripheral undo");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            hardware = false,
            checks
        });
        foreach (var session in editorDocuments)
        {
            session.Source = session.Source with
            {
                Text = session.Buffer.Text
            };
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}
