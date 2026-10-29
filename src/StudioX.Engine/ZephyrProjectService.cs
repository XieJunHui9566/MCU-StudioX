namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>从 MCU 创建自定义板级工程，并保留历史专用包的兼容读取与生成；不触碰硬件。</summary>
public sealed class ZephyrProjectService(Func<string, CancellationToken, Task>? initializeRepository = null)
{
    internal static StudioXException RuntimeUnavailable() => new("ZEPHYR_RUNTIME_UNAVAILABLE",
        "Zephyr 实验工程运行时尚未准备；当前仅支持离线创建与打开，不能使用裸机后端构建、下载或调试。");

    /// <summary>从创建时的板级模板映射定位工程内的 DTS 源文件，不把未合成的板级 DTS 当作完整设备树。</summary>
    public async Task<string?> FindBoardDevicetreeAsync(string directory, ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        if (project.Zephyr is { FormatVersion: 2 })
        {
            await ZephyrProjectSettings.ValidateAsync(directory, project, cancellationToken);
            return project.Zephyr.Devicetree;
        }
        var manifest = await ZephyrProjectSettings.ReadValidatedManifestAsync(directory, project, cancellationToken);
        var board = manifest.Boards.Single(item => item.Id == project.DeviceId);
        var template = board.Templates.Single(item => item.Id == project.TemplateId);
        if (template.Files is null)
        {
            return null;
        }
        var boardName = project.Zephyr!.BoardTarget.Split('/')[0].Split('@')[0] + ".dts";
        foreach (var relative in template.Files.Keys
            .Where(path => path.StartsWith("boards/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path).Equals(boardName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(PathBoundary.Resolve(directory, relative)))
            {
                return relative;
            }
        }
        return null;
    }

    /// <summary>从准确 MCU 创建工程自带的板级配置；上游 SDK 与 Python 不复制进用户工程。</summary>
    public async Task<ProjectManifest> CreateFromPackAsync(InstalledPack selected, string deviceId,
        string name, string destination, CancellationToken token = default)
    {
        PackValidator.Token(name);
        var pack = await PackRepository.VerifyAsync(selected, token);
        var device = pack.Manifest.Devices.SingleOrDefault(d => d.Id == deviceId)
            ?? throw new StudioXException("PROJECT_DEVICE", "请选择明确的芯片型号。");
        var profile = ZephyrDeviceCatalog.ForDevice(device)
            ?? throw new StudioXException("ZEPHYR_DEVICE", "此器件身份、容量或架构与 Zephyr 模板核验记录不同。");
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new StudioXException("PROJECT_EXISTS", "目标路径已存在，请选择新的工程目录。");
        }
        var board = "studiox_" + device.Id.ToLowerInvariant();
        var boardRoot = "boards/others/" + board + "/";
        var project = new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
            device.Id, ZephyrDeviceCatalog.TemplateId, ZephyrDeviceCatalog.ToolsetId, ZephyrDeviceCatalog.ToolsetVersion,
            ZephyrDeviceCatalog.CompilerId, ProjectKind.Zephyr, EntryFile: "src/main.c",
            Zephyr: new(2, board, board, "custom", ZephyrDeviceCatalog.ZephyrVersion, false, boardRoot + board + ".dts"),
            DevelopmentComponents: [ZephyrDeviceCatalog.Requirement]);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            async Task WriteAsync(string relative, string content)
            {
                var path = PathBoundary.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content.Replace("\r\n", "\n") + "\n", token);
            }
            await WriteAsync("CMakeLists.txt", """
                cmake_minimum_required(VERSION 3.20.0)
                # 板级配置随工程保存，可以按自己的 PCB 调整。
                list(APPEND BOARD_ROOT ${CMAKE_CURRENT_SOURCE_DIR})
                find_package(Zephyr REQUIRED HINTS $ENV{ZEPHYR_BASE})
                project(studiox_app LANGUAGES C CXX ASM)
                target_sources(app PRIVATE src/main.c)
                """);
            await WriteAsync("src/main.c", """
                #include <zephyr/kernel.h>

                int main(void)
                {
                    // 不假定板上的 LED 或串口接线；配置完成后可在此添加应用逻辑。
                    while (1)
                    {
                        k_sleep(K_MSEC(1000));
                    }
                    return 0;
                }
                """);
            await WriteAsync("prj.conf", """
                CONFIG_MAIN_STACK_SIZE=1024
                CONFIG_CONSOLE=n
                CONFIG_SERIAL=n
                CONFIG_PRINTK=n
                """);
            await WriteAsync("app.overlay", "// 按实际 PCB 添加外设、pinctrl、aliases 和 chosen；不要套用其他开发板引脚。");
            await WriteAsync(boardRoot + "board.yml", $"board:\n  name: {board}\n  full_name: StudioX {device.Id} custom board\n  vendor: others\n  socs:\n    - name: {profile.Soc}");
            await WriteAsync(boardRoot + "Kconfig." + board, $"config BOARD_{board.ToUpperInvariant()}\n\tselect {profile.SocConfig}");
            await WriteAsync(boardRoot + board + "_defconfig", "# 外设由用户按实际板卡启用；不默认开启 UART 控制台。");
            await WriteAsync(boardRoot + board + ".dts", $$"""
                /dts-v1/;
                #include <{{profile.Dtsi}}>

                / {
                    model = "StudioX {{device.Id}} custom board";
                    compatible = "studiox,{{board.Replace('_', '-')}}";
                    chosen {
                        zephyr,sram = &sram0;
                        zephyr,flash = &flash0;
                    };
                };

                // 内部 HSI 起步，不猜测外部晶振。改时钟时同时核对电压、总线和 Flash 等待周期。
                &clk_hsi { status = "okay"; };
                &rcc {
                    clocks = <&clk_hsi>;
                    clock-frequency = <{{profile.InternalClockHz}}>;
                    ahb-prescaler = <1>;
                    apb1-prescaler = <1>;
                    apb2-prescaler = <1>;
                };
                """);
            await WriteAsync("README.md", $"# {name}\n\n器件：{device.Id}；Zephyr {ZephyrDeviceCatalog.ZephyrVersion}。\n\n" +
                "在 MCU StudioX 中使用编译按钮。缺少锁定组件时，从开发环境组件管理导入 Zephyr 组件。\n\n" +
                $"- `{boardRoot}{board}.dts`：板上时钟、内存与外设。初始使用 HSI，无外部引脚假设。\n" +
                "- `app.overlay`：按实际电路添加引脚和外部器件。使用 pinctrl 前，请显式 include 对应封装的上游 pinctrl 文件。\n" +
                "- `prj.conf`：应用和驱动选项。添加源文件后同步修改 CMakeLists.txt。\n\n" +
                "编译通过不代表实板验证。下载和调试尚未接入；不可使用其他开发板的烧录参数。\n");
            await WriteAsync(".gitignore", ".build/\nbuild/\n.studiox/toolchain.lock.json\n.studiox/development-components.lock.json");
            await JsonStore.WriteAsync(PathBoundary.Resolve(staging, ".studiox/zephyr-device.json"), device, token);
            await JsonStore.WriteAsync(PathBoundary.Resolve(staging, ".studiox/project.json"), project, token);
            _ = await ProjectService.ReadAsync(staging, token);
            if (initializeRepository is not null)
            {
                await initializeRepository(staging, token);
            }
            await ProjectService.PublishDirectoryAsync(staging, target, token);
            return project;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public async Task<ProjectManifest> CreateAsync(InstalledZephyrPack selected, string boardId, string templateId,
        string name, string destination, CancellationToken cancellationToken = default)
    {
        PackValidator.Token(name);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new StudioXException("PROJECT_EXISTS", "目标路径已存在，请选择新的工程目录。");
        }

        // 目录浏览结果只是候选；复制前重新核对安装内容的完整指纹与全部模板文件。
        var pack = await ZephyrPackRepository.VerifyAsync(selected, cancellationToken);
        var board = pack.Manifest.Boards.SingleOrDefault(item => item.Id == boardId)
            ?? throw new StudioXException("PROJECT_ZEPHYR_BOARD", "请选择包中明确的 Zephyr 板级目标。");
        var template = board.Templates.SingleOrDefault(item => item.Id == templateId)
            ?? throw new StudioXException("PROJECT_ZEPHYR_TEMPLATE", "请选择此板卡提供的 Zephyr 工程模板。");
        var project = new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
            board.Id, template.Id, "", "", "", ProjectKind.Zephyr, EntryFile: "src/main.c",
            Zephyr: new(1, board.Id, board.BoardTarget, board.BoardRevision, pack.Manifest.ZephyrVersion,
                pack.Manifest.Experimental));
        ZephyrProjectSettings.Validate(project);

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var (relative, source) in template.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = PathBoundary.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(PathBoundary.Resolve(pack.RootDirectory, source), output);
            }
            if (File.Exists(Path.Combine(staging, ".gitignore")))
            {
                throw new StudioXException("PROJECT_RESERVED_FILE", "Zephyr 模板占用了工程生成器路径：.gitignore");
            }
            Directory.CreateDirectory(Path.Combine(staging, ".studiox"));
            File.Copy(PathBoundary.Resolve(pack.RootDirectory, ZephyrPackValidator.ManifestFile),
                Path.Combine(staging, ".studiox", "zephyr-pack.json"));
            await JsonStore.WriteAsync(Path.Combine(staging, ".studiox", "project.json"), project, cancellationToken);
            _ = await ProjectService.ReadAsync(staging, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"),
                "build/\n.build/\ncmake-build-*/\n.studiox/debug.json\n.studiox/breakpoints.json\n*.user\n", cancellationToken);
            if (initializeRepository is not null)
            {
                await initializeRepository(staging, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return project;
        }
        finally { if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); } }
    }
}
