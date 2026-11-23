namespace StudioX.Engine;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>从真实 VE 转换结果生成系统层；用户入口、厂商库与生成文件分别维护。</summary>
public static class Ag32SystemSupport
{
    public const string HeaderPath = "device/studiox/StudioX_System.h";
    public const string SourcePath = "device/studiox/StudioX_System.c";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GenerationLocks = new(StringComparer.OrdinalIgnoreCase);
    private const string StatePath = ".studiox/ag32-system-files.json";
    private const string ModuleIncludes = "target_include_directories(studiox_device BEFORE INTERFACE \"${CMAKE_CURRENT_LIST_DIR}/../include\")";
    internal const string Marker = "/* StudioX generated AG32 system support v1.";
    public const string CMakeBlock = """
        # StudioX AG32 system support
        target_sources(studiox_device INTERFACE "${CMAKE_CURRENT_LIST_DIR}/studiox/StudioX_System.c")
        target_include_directories(studiox_device INTERFACE "${CMAKE_CURRENT_LIST_DIR}/studiox")
        """;
    private const string PeripheralCMake = """
        # StudioX AG32 complete peripheral drivers v1
        file(GLOB STUDIOX_AG32_DRIVERS CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/studiox/vendor/*.c")
        target_sources(studiox_device INTERFACE ${STUDIOX_AG32_DRIVERS})
        target_include_directories(studiox_device SYSTEM INTERFACE "${CMAKE_CURRENT_LIST_DIR}/studiox/vendor")
        target_compile_definitions(studiox_device INTERFACE AGM_BOARD_INFO_H="StudioX_Board.h")
        """;

    /// <summary>迁移前核对生成凭据及全部系统文件；修改过的生成代码不能被副本重建静默丢弃。</summary>
    public static async Task<IReadOnlyList<string>> CheckMigrationFilesAsync(string root, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(root, StatePath);
        if (!File.Exists(path))
        {
            throw new StudioXException("AG32_SYSTEM_MODIFIED", "缺少系统生成文件记录，需要先核对系统文件。");
        }
        var declared = await JsonStore.ReadAsync<Dictionary<string, string>>(path, token);
        var known = Render([], null, []).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var module in Ag32PeripheralModule.All.Where(m => declared.ContainsKey(m.Source)))
        {
            known.Add(declared.ContainsKey(module.LegacyHeader) ? module.LegacyHeader : module.Header);
            known.Add(module.Source);
        }
        if (declared.Count != known.Count || declared.Keys.Any(key => !known.Contains(key)))
        {
            throw new StudioXException("AG32_SYSTEM_MODIFIED", "系统生成文件记录与受管目录不一致，需要人工审阅。");
        }
        foreach (var (relative, expected) in declared)
        {
            var file = PathBoundary.Resolve(root, relative);
            if (!File.Exists(file) || Hash(await File.ReadAllBytesAsync(file, token)) != expected)
            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", "系统生成文件已改变或缺失，需要保留修改：" + relative);
            }
        }
        return known.ToArray();
    }

    public static string RenderDeviceForMigration(BuildPlan plan) => CMakeGenerator.RenderDevice(plan) + "\n" + PeripheralCMake + "\n";

    public static bool MatchesDeviceForMigration(BuildPlan plan, string actual)
    {
        actual = actual.Replace("\n" + ModuleIncludes + "\n", "", StringComparison.Ordinal);
        foreach (var module in Ag32PeripheralModule.All)
        {
            actual = actual.Replace("\n" + module.CMake + "\n", "", StringComparison.Ordinal);
        }
        return actual == RenderDeviceForMigration(plan);
    }

    public static bool IsManagedFile(string path, string text) =>
        ((path.Equals(HeaderPath, StringComparison.OrdinalIgnoreCase) || path.Equals(SourcePath, StringComparison.OrdinalIgnoreCase) ||
          Ag32PeripheralModule.All.Any(m => path.Equals(m.Header, StringComparison.OrdinalIgnoreCase) ||
              path.Equals(m.LegacyHeader, StringComparison.OrdinalIgnoreCase) || path.Equals(m.Source, StringComparison.OrdinalIgnoreCase)) ||
          path.Equals("device/studiox/StudioX_Board.h", StringComparison.OrdinalIgnoreCase)) && text.StartsWith(Marker, StringComparison.Ordinal)) ||
        (path.StartsWith(Ag32PeripheralSupport.VendorPath, StringComparison.OrdinalIgnoreCase) &&
         Ag32PeripheralSupport.Drivers().TryGetValue(path, out var raw) && Encoding.UTF8.GetString(raw) == text);

    internal static bool IsValidName(string name) =>
        Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,47}\z", RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(name, @"\A(?:GPIO|SYS|INT|BOARD|STUDIOX|Delay_|APB_|AHB_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Ag32PeripheralModule.All.Any(module => module.Init == name) &&
        !("auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false class public private protected virtual new delete template this namespace using nullptr main".Split(' ').Contains(name, StringComparer.Ordinal));

    internal static Dictionary<string, byte[]> Render(byte[] ve, string? vendorHeader, IReadOnlyList<Ag32PinFunction> functions)
    {
        var document = new Ag32PinPlanDocument(ve);
        var conflicts = Ag32PinPlanConflicts.Find(document.Assignments, functions);
        if (conflicts.Length != 0)
        {
            throw new StudioXException("AG32_PIN_PLAN_CONFLICT", string.Join("\n", conflicts.Select(item => item.Message)));
        }
        var configured = document.Clocks.SysMhz is not null;
        if (document.Clocks.BusMhz is not null && !configured ||
            Regex.IsMatch(Encoding.UTF8.GetString(ve), @"(?m)^\s*(?:SYSCLK|HSECLK|BUSCLK)\s*,", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "系统代码需要用空格分隔的时钟字段；设置 BUSCLK 时请同时明确 HSECLK 和 SYSCLK。");
        }
        // 新建工程可离线生成已核对的初始时钟。保存和构建仍读取本次厂商转换结果。
        var initialClocks = vendorHeader is null && document.Clocks == Ag32PinClockSettings.NewProject;
        if (configured && (document.Clocks.HseMhz is null || vendorHeader is null && !initialClocks))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "生成系统初始化时，请同时填写实际 HSECLK 和 SYSCLK，再保存并生成。");
        }
        uint Frequency(string name, uint fallback)
        {
            if (vendorHeader is null)
            {
                return fallback;
            }
            var match = Regex.Match(vendorHeader, @"(?m)^#define BOARD_" + name + @"_FREQUENCY\s+([0-9]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value) || value == 0 || value > 248_000_000)
            {
                throw new StudioXException("AG32_SYSTEM_CLOCK", "厂商转换结果缺少有效频率：" + name);
            }
            return value;
        }
        var hsi = Frequency("HSI", 10_000_000);
        var hse = Frequency("HSE", 8_000_000);
        var pll = Frequency("PLL", initialClocks ? (uint)(document.Clocks.SysMhz!.Value * 1_000_000m) : 100_000_000);
        var sys = configured ? pll : hsi;
        var busClock = Frequency("BUS", initialClocks ? (uint)(document.Clocks.BusMhz!.Value * 1_000_000m) : hsi);
        var bus = configured ? busClock : hsi;
        if (configured && (sys % bus != 0 || sys / bus > 256 ||
            vendorHeader is not null && !Regex.IsMatch(vendorHeader, @"(?m)^#define BOARD_PLL_CLKIN\s+PIN_HSE\s*$", RegexOptions.CultureInvariant)))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "当前自动系统初始化仅支持已校验的 HSE→PLL 和整数总线分频；此时钟配置不能自动生成。");
        }
        var config = $"#define STUDIOX_HSI_HZ {hsi}u\n#define STUDIOX_HSE_HZ {hse}u\n#define STUDIOX_SYSCLK_HZ {sys}u\n#define STUDIOX_BUSCLK_HZ {bus}u\n#define STUDIOX_CONFIGURE_PLL {(configured ? 1 : 0)}\n";
        var analog = Ag32PeripheralSupport.Read(ve);
        config += $"#define STUDIOX_PLL_HZ {pll}u\n";
        config += $"#define STUDIOX_ANALOG_SEPARATE_BUS {(document.Clocks.BusMhz is not null ? 1 : 0)}\n#define STUDIOX_ANALOG_BUS_HZ {busClock}u\n";
        config += $"#define STUDIOX_ANALOG_ENABLED {(analog.Enabled ? 1 : 0)}\n#define STUDIOX_ADC_CHANNEL_MASK 0x{analog.AdcChannels:X4}u\n#define STUDIOX_DAC_MASK {((analog.Dac0 ? 1 : 0) | (analog.Dac1 ? 2 : 0))}u\n#define STUDIOX_CMP_ENABLED {(analog.Comparator ? 1 : 0)}\n";
        var pins = new StringBuilder();
        var objects = new StringBuilder();
        var init = new StringBuilder();
        var gpioInit = new StringBuilder();
        var uartInit = new StringBuilder();
        var uartModules = Ag32UartInitialization.Read(ve).Select(settings => settings.Module).ToHashSet(StringComparer.Ordinal);
        var buses = Ag32BusInitialization.Read(ve);
        var busCode = buses.Select(s => s.Module[..^1]).Distinct().ToDictionary(type => type, _ => new StringBuilder());
        var instanceCode = buses.ToDictionary(s => s.Module, s => busCode[s.Module[..^1]]);
        var system = Ag32SystemPeripherals.Read(ve);
        var timers = Ag32TimerInitialization.Read(ve);
        var timerCode = new StringBuilder();
        foreach (var timer in timers.Where(t => Ag32TimerInitialization.IsGeneral(t.Module)))

        {
            instanceCode.Add(timer.Module, timerCode);
        }
        foreach (var uart in uartModules)
        {
            instanceCode.Add(uart, uartInit);
        }
        var enabledPeripherals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in document.Assignments)
        {
            if (functions.Any(function => function.IsVerilog && function.Name == pin.Function))
            {
                continue;
            }
            // 只打开已分配外设的时钟；不启动收发、不替用户决定波特率或启动看门狗。
            var peripheral = Regex.Match(pin.Function, @"\A(UART[0-4]|SPI[01]|I2C[01]|CAN0|GPTIMER[0-4]|MAC0|USB0)_", RegexOptions.CultureInvariant);
            var peripheralInit = instanceCode.GetValueOrDefault(peripheral.Groups[1].Value, init);
            if (peripheral.Success && enabledPeripherals.Add(peripheral.Groups[1].Value))
            {
                var module = peripheral.Groups[1].Value;
                var clockBus = module is "MAC0" or "USB0" ? "AHB" : "APB";
                peripheralInit.AppendLine($"    SYS_Enable{clockBus}Clock({clockBus}_MASK_{module});");
            }
            var gpio = Regex.Match(pin.Function, @"\AGPIO([0-9]+)_([0-7])\z", RegexOptions.CultureInvariant);
            var direct = gpio.Success;
            if (!direct)
            {
                gpio = Regex.Match(functions.FirstOrDefault(item => item.Name == pin.Function)?.SharedGpio ?? "", @"\AGPIO([0-9]+)_([0-7])\z", RegexOptions.CultureInvariant);
            }
            if (pin.Name is { } name)
            {
                pins.AppendLine($"/* {pin.Function} → PIN_{pin.PinNumber} */");
                if (direct)
                {
                    pins.AppendLine($"/* 电气配置：{Ag32GpioElectrical.Describe(pin)}，由映射镜像生效。 */");
                }
                pins.AppendLine($"#define {name}_Pin {pin.PinNumber}u");
                if (gpio.Success)
                {
                    var port = "GPIO" + gpio.Groups[1].Value;
                    var bit = "GPIO_BIT" + gpio.Groups[2].Value;
                    pins.AppendLine($"#define {name}_Port {port}\n#define {name}_Bit {bit}\n#define {name}_Clock APB_MASK_{port}\nextern const StudioX_Pin {name};");
                    objects.AppendLine($"const StudioX_Pin {name} = {{ {port}, {bit}, {pin.PinNumber}u }};");
                }
            }
            // 映射不等于输出授权：只有明确选择 INPUT / OUTPUT 才自动配置电气方向。
            if (direct && pin.Direction is "INPUT" or "OUTPUT")
            {
                var port = "GPIO" + gpio.Groups[1].Value;
                var bit = "GPIO_BIT" + gpio.Groups[2].Value;
                var mode = pin.Direction == "INPUT" ? "输入" : pin.InitialHigh ? "输出，初始高电平" : "输出，初始低电平";
                var electrical = pin.Direction == "INPUT" ? Ag32GpioElectrical.DescribePull(pin) : Ag32GpioElectrical.Describe(pin);
                gpioInit.AppendLine($"    /* PIN_{pin.PinNumber} 用作 {pin.Function} {mode}。映射镜像配置的电气属性为{electrical}。 */");
                gpioInit.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{port});\n    GPIO_SetSoftwareMode({port}, {bit});");
                if (pin.Direction == "OUTPUT")
                {
                    gpioInit.AppendLine($"    GPIO_Set{(pin.InitialHigh ? "High" : "Low")}({port}, {bit});");
                }
                gpioInit.AppendLine($"    GPIO_Set{(pin.Direction == "OUTPUT" ? "Output" : "Input")}({port}, {bit});");
            }
            else if (!direct && gpio.Success)
            {
                // 外设复用需切到硬件模式；只改本次已映射位，不误启用同组未分配 IO。
                var port = "GPIO" + gpio.Groups[1].Value;
                var bit = "GPIO_BIT" + gpio.Groups[2].Value;
                peripheralInit.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{port});\n    GPIO_SetHardwareMode({port}, {bit});");
            }
        }
        var result = Ag32PeripheralSupport.Drivers();
        var api = new StringBuilder();
        var handlers = new StringBuilder();
        Ag32GpioInterrupts.Generate(document.Assignments, api, handlers, gpioInit);
        if (gpioInit.Length != 0)
        {
            Ag32PeripheralModule.Gpio.Render(result, api.ToString(), handlers.ToString(), gpioInit.ToString());
            pins.Append(api); // 旧应用仍可从系统头文件获取事件计数和串口接收接口。
        }
        api.Clear();
        handlers.Clear();
        foreach (var uart in uartModules.Where(u => !enabledPeripherals.Contains(u)))
        {
            uartInit.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{uart});");
        }
        Ag32UartInitialization.Generate(ve, document.Assignments, bus, api, handlers, uartInit);
        if (uartModules.Count != 0)
        {
            Ag32PeripheralModule.Uart.Render(result, api.ToString(), handlers.ToString(), uartInit.ToString());
            pins.Append(api);
        }
        var timerApi = new StringBuilder();
        var timerHandlers = new StringBuilder();
        Ag32TimerInitialization.BasicHandlers(timers, timerApi, timerHandlers);
        foreach (var timer in system.Setup.OrderTimers(timers))
        {
            Ag32TimerInitialization.GeneralHandler(timer, timerApi, timerHandlers);
            var module = timer.Module.Split('.')[0];
            if (enabledPeripherals.Add(module))
            {
                timerCode.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{module});");
            }
            var code = Ag32TimerInitialization.InitCode(timer, document.Assignments, bus, out _, system.Setup, deferStart: system.Setup.Units.ContainsKey(timer.Module));
            foreach (var line in code.ReplaceLineEndings("\n").Split('\n'))
            {
                timerCode.Append("    ").AppendLine(line);
            }
        }
        // 先配置所有时基并发出预装载 UG，再使能从触发，避免初始化主定时器时提前发出首脉冲。
        foreach (var timer in system.Setup.OrderTimers(timers).Where(t => Ag32TimerInitialization.IsGeneral(t.Module) && system.Setup.Units.ContainsKey(t.Module)))

        {
            foreach (var line in system.Setup.TimerStart(timer.Module))
            {
                timerCode.Append("    ").AppendLine(line);
            }
        }
        if (timers.Length > 0)
        {
            Ag32PeripheralModule.Timer.Render(result, timerApi.ToString(), timerHandlers.ToString(), timerCode.ToString());
        }
        foreach (var settings in buses)
        {
            var code = Ag32BusInitialization.InitCode(settings, document.Assignments, bus, system.Setup);
            foreach (var line in code.ReplaceLineEndings("\n").Split('\n'))

            {
                instanceCode[settings.Module].Append("    ").AppendLine(line);
            }
        }
        foreach (var module in Ag32PeripheralModule.All)

        {
            if (busCode.TryGetValue(module.Name, out var body))
            {
                api.Clear();
                handlers.Clear();
                foreach (var settings in buses.Where(s => s.Module[..^1] == module.Name))

                {
                    Ag32BusInterrupts.Generate(settings, api, handlers, system.Setup);
                }
                if (module.Name == "SPI")
                {
                    system.Setup.GenerateSpiProfiles(api, handlers, buses);
                    Ag32SpiDma.Generate(buses, api, handlers, body, system.Setup);
                }
                module.Render(result, api.ToString(), handlers.ToString(), body.ToString());
            }
        }
        system.Render(result, bus);
        system.Setup.Generate(result, timers, buses.Any(Ag32SpiDma.Enabled));
        result[HeaderPath] = Encoding.UTF8.GetBytes(Resource("h").Replace("@@CONFIG@@", config).Replace("@@PINS@@", pins.ToString()));
        result[SourcePath] = Encoding.UTF8.GetBytes(Resource("c").Replace("@@PIN_OBJECTS@@", objects.ToString()).Replace("@@PIN_INIT@@", init.ToString()));
        result["device/studiox/StudioX_Board.h"] = Encoding.UTF8.GetBytes(Marker + " Generated clock metadata. */\n#pragma once\n" +
            $"#define BOARD_HSI_FREQUENCY {hsi}u\n#define BOARD_HSE_FREQUENCY {hse}u\n#define BOARD_PLL_FREQUENCY {pll}u\n#define BOARD_BUS_FREQUENCY {bus}u\n");
        return result;
    }

    internal static async Task<(string[] Updated, Dictionary<string, string> Moved)> WriteAsync(string root, Dictionary<string, byte[]> generated, string vePath, byte[] expectedVe,
        byte[]? newVe, CancellationToken token)
    {
        var gate = GenerationLocks.GetOrAdd(Path.GetFullPath(root), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            return await WriteCoreAsync(root, generated, vePath, expectedVe, newVe, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(string[] Updated, Dictionary<string, string> Moved)> WriteCoreAsync(string root, Dictionary<string, byte[]> generated, string vePath,
        byte[] expectedVe, byte[]? newVe, CancellationToken token)
    {
        // 在任何写入之前检查所有受管文件；外部编辑不能被下一次图形保存静默覆盖。
        Ag32PeripheralSupport.VerifySdk(root);
        var statePath = PathBoundary.Resolve(root, StatePath);
        var previous = File.Exists(statePath) ? await JsonStore.ReadAsync<Dictionary<string, string>>(statePath, token) : [];
        var active = Ag32PeripheralModule.All.Where(m => generated.ContainsKey(m.Source)).ToArray();
        // 停用模块时保留空入口，使已有业务代码的包含和调用继续有效。
        foreach (var module in Ag32PeripheralModule.All.Where(m => previous.ContainsKey(m.Source) && !generated.ContainsKey(m.Source)))

        {
            module.Render(generated);
        }
        var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in Ag32PeripheralModule.All.Where(m => generated.ContainsKey(m.Header)))
        {
            var legacy = PathBoundary.Resolve(root, module.LegacyHeader);
            var target = PathBoundary.Resolve(root, module.Header);
            if (!File.Exists(legacy))
            {
                continue;
            }
            var bytes = await File.ReadAllBytesAsync(legacy, token);
            if (!previous.TryGetValue(module.LegacyHeader, out var hash) || Hash(bytes) != hash)

            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", $"{module.LegacyHeader} 已被修改或为用户文件，会遮蔽 {module.Header}。请先处理同名文件。");
            }
            if (File.Exists(target) || Directory.Exists(target))

            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", $"无法移动 {module.LegacyHeader}：{module.Header} 已存在。请先处理同名文件。");
            }
            moves.Add(module.LegacyHeader, module.Header);
            before[module.Header] = bytes;
        }
        foreach (var (relative, bytes) in generated)
        {
            if (before.ContainsKey(relative))
            {
                continue;
            }
            var path = PathBoundary.Resolve(root, relative);
            var old = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
            var expected = previous.GetValueOrDefault(relative);
            // 已将旧头文件移到 include 的工程仍可按旧凭据校验，继续未完成的迁移。
            if (old is not null && expected is null && Ag32PeripheralModule.All.FirstOrDefault(m => m.Header == relative) is { } module)

            {
                expected = previous.GetValueOrDefault(module.LegacyHeader);
            }
            if (old is not null && expected != Hash(old))
            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", relative + " 已被手动修改或被同名文件占用；请保留修改并移出生成目录后重试。");
            }
            before[relative] = old;
        }
        var updates = new Dictionary<string, byte[]>(generated, StringComparer.Ordinal);
        var previousModules = Ag32PeripheralModule.All.Where(m => previous.ContainsKey(m.Source)).ToArray();
        var manifest = await ProjectService.ReadAsync(root, token);
        var entry = active.Length != 0 || previousModules.Length != 0
            ? LocateMain(root, manifest) : FindMain(root, manifest);
        if (entry is not null)
        {
            var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, entry), token);
            before[entry] = bytes;
            updates[entry] = Ag32MainInitialization.Insert(bytes, active, previousModules);
        }
        var cmakePath = PathBoundary.Resolve(root, CMakeGenerator.DeviceListPath);
        if (File.Exists(cmakePath))
        {
            var bytes = await File.ReadAllBytesAsync(cmakePath, token);
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.Contains(CMakeBlock, StringComparison.Ordinal))
            {
                if (!CMakeGenerator.IsManagedFile(CMakeGenerator.DeviceListPath, text))
                {
                    throw new StudioXException("AG32_SYSTEM_CMAKE", "器件 CMake 不属于系统管理，不能自动接入系统文件。");
                }
                updates[CMakeGenerator.DeviceListPath] = Encoding.UTF8.GetBytes(text + "\n" + CMakeBlock + "\n");
                before[CMakeGenerator.DeviceListPath] = bytes;
            }
            if (!text.Contains(PeripheralCMake, StringComparison.Ordinal))
            {
                if (!CMakeGenerator.IsManagedFile(CMakeGenerator.DeviceListPath, text))
                {
                    throw new StudioXException("AG32_SYSTEM_CMAKE", "器件 CMake 不是受管文件，不能接入完整外设驱动。");
                }
                var current = updates.TryGetValue(CMakeGenerator.DeviceListPath, out var updated) ? Encoding.UTF8.GetString(updated) : text;
                updates[CMakeGenerator.DeviceListPath] = Encoding.UTF8.GetBytes(current + "\n" + PeripheralCMake + "\n");
                before[CMakeGenerator.DeviceListPath] = bytes;
            }
            var deviceCmake = updates.TryGetValue(CMakeGenerator.DeviceListPath, out var currentBytes) ? Encoding.UTF8.GetString(currentBytes) : text;
            var moduleCommands = Ag32PeripheralModule.All.Where(m => generated.ContainsKey(m.Source)).Select(m => m.CMake).ToArray();
            foreach (var command in moduleCommands.Length == 0 ? moduleCommands : moduleCommands.Prepend(ModuleIncludes))
            {
                if (deviceCmake.Contains(command, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!CMakeGenerator.IsManagedFile(CMakeGenerator.DeviceListPath, text))

                {
                    throw new StudioXException("AG32_SYSTEM_CMAKE", "无法向非受管的器件 CMake 登记外设：" + command);
                }
                deviceCmake += "\n" + command + "\n";
                updates[CMakeGenerator.DeviceListPath] = Encoding.UTF8.GetBytes(deviceCmake);
                before[CMakeGenerator.DeviceListPath] = bytes;
            }
        }
        updates[StatePath] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(generated.ToDictionary(item => item.Key, item => Hash(item.Value)), JsonStore.Options);
        before[StatePath] = File.Exists(statePath) ? await File.ReadAllBytesAsync(statePath, token) : null;
        if (newVe is not null)
        {
            updates[vePath] = newVe;
            before[vePath] = expectedVe;
        }
        var currentVe = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, vePath), token);
        if (!expectedVe.SequenceEqual(currentVe))
        {
            throw new StudioXException("AG32_PIN_PLAN_STALE", "VE 在系统代码生成期间发生变化，请重新读取。");
        }
        var written = new List<string>();
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (from, to) in moves)
            {
                var source = PathBoundary.Resolve(root, from);
                var target = PathBoundary.Resolve(root, to);
                if (!(await File.ReadAllBytesAsync(source, token)).SequenceEqual(before[to]!))

                {
                    throw new StudioXException("AG32_SYSTEM_MODIFIED", from + " 在迁移期间发生变化。");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                moved.Add(from, to);
            }
            foreach (var (relative, bytes) in updates)
            {
                token.ThrowIfCancellationRequested();
                var path = PathBoundary.Resolve(root, relative);
                var old = before[relative];
                if (old is not null && old.SequenceEqual(bytes))
                {
                    continue;
                }
                var current = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
                if ((current is null) != (old is null) || old is not null && !old.SequenceEqual(current!))
                {
                    throw new StudioXException("AG32_SYSTEM_MODIFIED", relative + " 在生成期间发生变化，未覆盖。");
                }
                await ReplaceAsync(path, bytes, token);
                written.Add(relative);
            }
        }
        catch
        {
            foreach (var relative in written.AsEnumerable().Reverse())
            {
                var path = PathBoundary.Resolve(root, relative);
                if (!File.ReadAllBytes(path).SequenceEqual(updates[relative]))
                {
                    continue;
                }
                if (before[relative] is { } old)
                {
                    await ReplaceAsync(path, old, CancellationToken.None);
                }
                else
                {
                    File.Delete(path);
                }
            }
            foreach (var (from, to) in moved.Reverse())
            {
                var source = PathBoundary.Resolve(root, from);
                var target = PathBoundary.Resolve(root, to);
                if (!File.Exists(source) && File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(before[to]!))

                {
                    File.Move(target, source);
                }
            }
            throw;
        }
        return (written.Concat(moved.Values).Distinct(StringComparer.Ordinal).ToArray(), moved);
    }

    public static string LocateMain(string root, ProjectManifest project) => FindMain(root, project) ??
        throw new StudioXException("AG32_MAIN_INIT", "请为工程指定唯一的 main 入口文件，以生成外设初始化调用。");

    public static string? FindMain(string root, ProjectManifest project)
    {
        if (project.EntryFile is { } entry && File.Exists(PathBoundary.Resolve(root, entry)))
        {
            return entry;
        }
        var candidates = new[] { "src/main.c", "src/main.cpp", "src/main.cc", "src/main.cxx" }
            .Where(file => File.Exists(PathBoundary.Resolve(root, file))).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static async Task RestoreModuleOwnershipAsync(string original, string copy, CancellationToken token)
    {
        var receipt = await JsonStore.ReadAsync<Dictionary<string, string>>(PathBoundary.Resolve(original, StatePath), token);
        var path = PathBoundary.Resolve(copy, StatePath);
        var target = await JsonStore.ReadAsync<Dictionary<string, string>>(path, token);
        foreach (var file in Ag32PeripheralModule.All.SelectMany(module => new[] { module.Source, module.Header, module.LegacyHeader }).Where(receipt.ContainsKey))
        {
            if (Hash(await File.ReadAllBytesAsync(PathBoundary.Resolve(copy, file), token)) != receipt[file])

            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", "升级副本的外设文件已改变：" + file);
            }
            target[file] = receipt[file];
        }
        await JsonStore.WriteAsync(path, target, token);
    }

    private static async Task ReplaceAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Resource(string extension)
    {
        using var stream = typeof(Ag32SystemSupport).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.Ag32.StudioX_System." + extension)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
