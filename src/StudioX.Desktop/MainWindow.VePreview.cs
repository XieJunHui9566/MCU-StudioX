namespace StudioX.Desktop;

using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在小型工程副本中验证 VE 实际渲染及保存；不修改用户工程或启动逻辑工具。</summary>
    public async Task RenderVePreviewAsync(string directory, string project, string relativePath)
    {
        var original = await services.Files.ReadAsync(project, relativePath);
        var originalPath = PathBoundary.Resolve(project, relativePath);
        var originalBytes = await File.ReadAllBytesAsync(originalPath);
        var fixture = Path.Combine(directory, "fixture");
        foreach (var path in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c", "CMakeLists.txt", relativePath }.Distinct())
        {
            if (!services.Files.FileExists(project, path))
            {
                continue;
            }
            var output = PathBoundary.Resolve(fixture, path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, await File.ReadAllBytesAsync(PathBoundary.Resolve(project, path)));
        }

        const string clockPath = "logic/editor-clock-check.ve";
        const string verilogPath = "logic/editor-verilog-check.v";
        const string clockText = "# 编辑器验证数据，不作为器件配置\r\nSYSCLK 200\r\nBUSCLK 100\r\nHSECLK 8\r\n\r\nGPIO4_4 PIN_21 # 行尾注释\r\nDATA[0] PIN_22:INPUT\r\nLED PIN_23:OUTPUT\r\nIO PIN_24:INOUT\r\n";
        Directory.CreateDirectory(Path.Combine(fixture, "logic"));
        await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, clockPath), clockText, new UTF8Encoding(true));
        await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, verilogPath), "// 文档切换验证\nmodule editor_check(input wire clk, output wire led);\nassign led = clk;\nendmodule\n");

        await OpenProjectAsync(fixture, CancellationToken.None);
        async Task LayoutAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        try
        {
            foreach (var path in new[] { relativePath, clockPath })
            {
                await OpenSourceAsync(path, CancellationToken.None);
                var session = activeEditor!;
                Check(CodeLanguage.ForFile(path) == "AGM Pin Map" && !SourceEditor.IsReadOnly,
                    "VE 文件未以可编辑配置文本打开。");
                foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
                {
                    ApplyTheme(theme);
                    await LayoutAsync();
                    Render(this, Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-" + theme.Id + ".png"));
                }
                Check(session.Buffer.Text == (path == relativePath ? original.Text : clockText),
                    "打开或切换主题改变了 VE 配置内容。");
                if (path == clockPath)
                {
                    session.Buffer.Insert(session.Buffer.TextLength, "# BOM/CRLF 保存检查\r\n");
                    await SaveEditorAsync(fixture, session, CancellationToken.None);
                    SourceEditor.Undo();
                    await SaveEditorAsync(fixture, session, CancellationToken.None);
                    await CloseWorkspaceTabAsync(session.Tab);
                    await OpenSourceAsync(clockPath, CancellationToken.None);
                    await LayoutAsync();
                    Check(activeEditor!.Buffer.Text == clockText && activeEditor.Source.Encoding.GetPreamble().Length == 3,
                        "BOM/CRLF 配置编辑、撤销、保存和重新打开后未保持原始格式。");
                }
            }

            await OpenSourceAsync(relativePath, CancellationToken.None);
            var ve = activeEditor!;
            var newline = ve.Buffer.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var suffix = newline + "# 编辑器保存回归检查" + newline;
            ve.Buffer.Insert(ve.Buffer.TextLength, suffix);
            await LayoutAsync();
            await OpenSourceAsync(verilogPath, CancellationToken.None);
            await LayoutAsync();
            await OpenSourceAsync("src/main.c", CancellationToken.None);
            await LayoutAsync();
            await OpenSourceAsync(relativePath, CancellationToken.None);
            await LayoutAsync();
            Check(ReferenceEquals(activeEditor, ve) && ve.IsDirty && ve.Buffer.Text == original.Text + suffix,
                "切换 C/Verilog/VE 后丢失编辑缓冲区。");
            await SaveEditorAsync(fixture, ve, CancellationToken.None);
            Check(!ve.IsDirty, "VE 保存后仍标记为未保存。");
            await CloseWorkspaceTabAsync(ve.Tab);
            await OpenSourceAsync(relativePath, CancellationToken.None);
            await LayoutAsync();
            Check(activeEditor!.Buffer.Text == original.Text + suffix, "VE 保存并重新打开后内容不一致。");

            // 只在副本中撤去测试注释，核对 BOM、换行与所有频率/引脚设置原样保留。
            activeEditor.Buffer.Remove(original.Text.Length, suffix.Length);
            await SaveEditorAsync(fixture, activeEditor, CancellationToken.None);
            Check((await File.ReadAllBytesAsync(PathBoundary.Resolve(fixture, relativePath))).SequenceEqual(originalBytes),
                "VE 编辑保存改变了原始编码、换行或配置字节。");
            Check((await File.ReadAllBytesAsync(PathBoundary.Resolve(fixture, clockPath)))
                .SequenceEqual(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(clockText))),
                "BOM/CRLF 频率配置夹具字节被改变。");
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(originalPath))) == original.DiskHash,
                "验证期间真实 VE 文件被修改。");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
                $"PASS: real AG32 VE and BOM/CRLF clock fixture rendered in dark/light; comments, blank lines, frequencies and pin mappings; C/Verilog/VE switching; edit/save/close/reopen; original bytes preserved. User VE SHA-256: {original.DiskHash}. Only fixture copies were written.\n");
        }
        finally
        {
            ClearEditorDocuments();
            ShowDocument(WelcomeTab);
        }
    }
}
