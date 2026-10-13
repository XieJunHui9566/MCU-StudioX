namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.BuildConfiguration;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>真实编辑缓冲区、文件通知与窗口的离线验收，不调用构建或设备连接。</summary>
    public async Task RenderSourceRegistrationPreviewAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            File.WriteAllLines(Path.Combine(output, "progress.txt"), checks);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        async Task Wait(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!condition())
            {
                await Task.Delay(25, timeout.Token);
            }
            await projectSyncTask.WaitAsync(timeout.Token);
        }
        async Task<string> Fixture(string name)
        {
            var root = Path.Combine(output, name);
            Directory.CreateDirectory(Path.Combine(root, ".studiox"));
            Directory.CreateDirectory(Path.Combine(root, "device"));
            Directory.CreateDirectory(Path.Combine(root, "src/nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), "int main(void) { return 0; }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src/nested/helper.c"), "int helper;\n");
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "# user source list\r\nadd_executable(app src/main.c src/nested/helper.c)\r\nadd_library(other STATIC src/main.c)\r\n");
            await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), new ProjectManifest(1, "source-ui", "offline.pack", "1.0.0", "hash", "offline", "plain", "offline.tools", "1.0.0", "gcc"));
            var device = new DeviceDefinition("offline", "Offline", "arm", 0x08000000, 65536, 0x20000000, 16384, "offline.tools", "1.0.0", "gcc", [], [], [], [], "linker.ld", [], [], [new ProjectTemplate("plain", "Plain", "Offline", "src/main.c")]);
            await JsonStore.WriteAsync(Path.Combine(root, "device/manifest.json"), new PackManifest(1, "offline.pack", "1.0.0", "Offline", "Offline", [device]));
            return root;
        }
        var project = await Fixture("中文 源码登记");
        await OpenProjectAsync(project, CancellationToken.None);
        Check(workbenchCommands.Any(command => command.Title == "源码登记与编译列表…") && workbenchCommands.Any(command => command.Title == "撤销上次源码登记") &&
            sourceContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "源码登记与编译列表…") && explorerMenu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "源码登记与编译列表…"), "tools, command palette, source and tree menus expose registration and grouped undo");
        await services.Files.CreateEntryAsync(project, "src", "新增 空格.c", false);
        await File.WriteAllTextAsync(Path.Combine(project, "src/新增 空格.c"), "int added;\n");
        await Wait(() => sourceRegistrationMenu!.Header!.ToString()!.Contains("待核对", StringComparison.Ordinal));
        var disk = await services.Files.ReadAsync(project, "CMakeLists.txt");
        Check(disk.Text.Contains("helper.c", StringComparison.Ordinal) && !disk.Text.Contains("新增", StringComparison.Ordinal), "file notifications mark pending review without editing compile configuration");
        await OpenSourceAsync("CMakeLists.txt", CancellationToken.None);
        var editor = activeEditor!;
        editor.Buffer.Insert(editor.Buffer.TextLength, "# original unsaved CMake\r\n");
        var unsaved = editor.Buffer.Text;
        var context = await services.SourceRegistration.ReadAsync(project, "CMakeLists.txt", CaptureWorkspaceBuffers());
        var inventory = await services.SourceRegistration.DiscoverAsync(project);
        SourceRegistrationPlan plan;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var window = new SourceRegistrationWindow(services.SourceRegistration, context, inventory.Configurations, inventory.Sources, sourceRegistrationMoves.ToArray(), CaptureWorkspaceBuffers, "src/新增 空格.c", Log)
            {
                Owner = this,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40
            };
            window.Show();
            await Settle();
            Check(window.Target.SelectedItem is null && !window.Apply.IsEnabled, "multiple targets require explicit selection in " + theme.Id);
            window.Target.SelectedItem = "app";
            window.PreviewSelected();
            plan = window.Plan!;
            await Settle();
            window.UpdateLayout();
            Check(window.Apply.IsEnabled && plan.Operations.Single().Kind == SourceRegistrationKind.Add && window.Before.Text == unsaved && window.After.Text.Contains("新增 空格.c", StringComparison.Ordinal), "selected new source previews unsaved CMake and explicit target in " + theme.Id);
            Render(window, Path.Combine(output, "source-registration-" + theme.Id + ".png"));
            window.Width = 760;
            window.Height = 540;
            await Settle();
            var button = window.Apply.TranslatePoint(new Point(), window);
            Check(button.X >= 0 && button.Y >= 0 && button.X + window.Apply.ActualWidth <= window.ActualWidth && button.Y + window.Apply.ActualHeight <= window.ActualHeight, "minimum window retains review/apply controls in " + theme.Id);
            Render(window, Path.Combine(output, "source-registration-" + theme.Id + "-compact.png"));
            window.Operations.SelectedItems.Clear();
            Check(window.Plan is null && !window.Apply.IsEnabled && window.After.Text.Length == 0, "changing selection invalidates prior preview in " + theme.Id);
            window.Close();
        }
        ApplyTheme(ThemeService.Dark);
        plan = services.SourceRegistration.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/新增 空格.c")]);
        await ApplySourceRegistrationAsync(plan, CancellationToken.None);
        Check(editor.Buffer.Text == plan.Change.After && editor.IsDirty && await File.ReadAllTextAsync(Path.Combine(project, "CMakeLists.txt")) == disk.Text && File.Exists(Path.Combine(project, "src/新增 空格.c")), "apply changes only CMake buffer and preserves disk plus physical source");
        editor.Buffer.UndoStack.Undo();
        Check(editor.Buffer.Text == unsaved, "ordinary editor undo reverses registration in one step and retains earlier unsaved notes");
        editor.Buffer.UndoStack.Redo();
        await UndoSourceRegistrationAsync(CancellationToken.None);
        Check(editor.Buffer.Text == unsaved, "registration undo retains preexisting unsaved CMake");
        await ApplySourceRegistrationAsync(plan, CancellationToken.None);
        editor.Buffer.Insert(editor.Buffer.TextLength, "# later edit\r\n");
        var later = editor.Buffer.Text;
        try
        {
            await UndoSourceRegistrationAsync(CancellationToken.None);
            throw new InvalidOperationException("Expected stale undo.");
        }
        catch (StudioXException error) when (error.Code == "SOURCE_REGISTRATION_STALE") { Check(editor.Buffer.Text == later, "grouped undo never overwrites later edits"); }
        editor.Buffer.UndoStack.Undo();
        await UndoSourceRegistrationAsync(CancellationToken.None);
        editor.Buffer.Insert(editor.Buffer.TextLength, "# stale preview\r\n");
        later = editor.Buffer.Text;
        try
        {
            await ApplySourceRegistrationAsync(plan, CancellationToken.None);
            throw new InvalidOperationException("Expected stale apply.");
        }
        catch (StudioXException error) when (error.Code == "WORKSPACE_EDIT_STALE") { Check(editor.Buffer.Text == later, "apply refuses a changed buffer without partial changes"); }
        editor.Buffer.UndoStack.Undo();
        SourceEditor.IsReadOnly = true;
        try
        {
            await ApplySourceRegistrationAsync(plan, CancellationToken.None);
            throw new InvalidOperationException("Expected read-only refusal.");
        }
        catch (StudioXException error) when (error.Code == "SOURCE_REGISTRATION_STALE") { Check(editor.Buffer.Text == unsaved, "active read-only editor cannot accept registration"); }
        SourceEditor.IsReadOnly = false;
        EventHandler fail = (_, _) => throw new InvalidOperationException("injected source registration editor event failure");
        editor.Buffer.TextChanged += fail;
        try
        {
            await ApplySourceRegistrationAsync(plan, CancellationToken.None);
            throw new InvalidOperationException("Expected editor event failure.");
        }
        catch (Exception error) when (error is AggregateException or StudioXException)
        {
            Check(editor.Buffer.Text == unsaved && error.ToString().Contains("injected source registration editor event failure", StringComparison.Ordinal), "edit event failure restores buffer and retains original diagnostics");
        }
        finally { editor.Buffer.TextChanged -= fail; }
        await ApplySourceRegistrationAsync(plan, CancellationToken.None);
        await SaveEditorAsync(project, editor, CancellationToken.None);
        Check(!editor.IsDirty && await File.ReadAllTextAsync(Path.Combine(project, "CMakeLists.txt")) == plan.Change.After, "explicit save persists registered compile configuration");
        var old = "src/nested/helper.c";
        Directory.Move(Path.Combine(project, "src/nested"), Path.Combine(project, "src/moved"));
        await Wait(() => sourceRegistrationMoves.Any(move => move.PreviousPath == "src/nested" && move.Path == "src/moved"));
        context = await services.SourceRegistration.ReadAsync(project, "CMakeLists.txt", CaptureWorkspaceBuffers());
        inventory = await services.SourceRegistration.DiscoverAsync(project);
        var operations = services.SourceRegistration.Suggest(context, "app", inventory.Sources, sourceRegistrationMoves.ToArray());
        Check(operations.Contains(new(SourceRegistrationKind.Rename, old, "src/moved/helper.c")), "native directory rename supplies registration candidate through unified synchronization");
        plan = services.SourceRegistration.Prepare(context, "app", [operations.Single(operation => operation.Kind == SourceRegistrationKind.Rename)]);
        await ApplySourceRegistrationAsync(plan, CancellationToken.None);
        Check(editor.Buffer.Text.Contains("src/moved/helper.c", StringComparison.Ordinal) && !editor.Buffer.Text.Contains(old, StringComparison.Ordinal), "directory rename updates only the selected target literal");
        await SaveEditorAsync(project, editor, CancellationToken.None);
        File.Delete(Path.Combine(project, "src/moved/helper.c"));
        await Wait(() => sourceRegistrationMenu!.Header!.ToString()!.Contains("待核对", StringComparison.Ordinal));
        context = await services.SourceRegistration.ReadAsync(project, "CMakeLists.txt", CaptureWorkspaceBuffers());
        operations = services.SourceRegistration.Suggest(context, "app", (await services.SourceRegistration.DiscoverAsync(project)).Sources, sourceRegistrationMoves.ToArray());
        Check(operations.Contains(new(SourceRegistrationKind.Remove, "src/moved/helper.c")), "external deletion exposes missing compile entry for review");
        plan = services.SourceRegistration.Prepare(context, "app", [new(SourceRegistrationKind.Remove, "src/moved/helper.c")]);
        var next = await Fixture("next-workspace");
        await OpenProjectAsync(next, CancellationToken.None, _ => MessageBoxResult.No);
        try
        {
            await ApplySourceRegistrationAsync(plan, CancellationToken.None);
            throw new InvalidOperationException("Expected project switch rejection.");
        }
        catch (StudioXException error) when (error.Code == "SOURCE_REGISTRATION_STALE") { Check(sourceRegistrationMoves.Count == 0 && sourceRegistrationUndo is null && activeEditor!.Buffer.Text.Contains("return 0", StringComparison.Ordinal), "project switch retires rename evidence and rejects old plans"); }
        await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.No);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            checks,
            hardware = false
        });
    }
}
