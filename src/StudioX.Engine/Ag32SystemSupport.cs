namespace StudioX.Engine;

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
    private const string StatePath = ".studiox/ag32-system-files.json";
    private const string Marker = "/* StudioX generated AG32 system support v1.";
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
        if (!File.Exists(path)) throw new StudioXException("AG32_SYSTEM_MODIFIED", "缺少系统生成文件记录，需要先核对系统文件。");
        var declared = await JsonStore.ReadAsync<Dictionary<string, string>>(path, token);
        var known = Render([], null, []).Keys.ToHashSet(StringComparer.Ordinal);
        if (declared.Count != known.Count || declared.Keys.Any(key => !known.Contains(key)))
            throw new StudioXException("AG32_SYSTEM_MODIFIED", "系统生成文件记录与受管目录不一致，需要人工审阅。");
        foreach (var (relative, expected) in declared)
        {
            var file = PathBoundary.Resolve(root, relative);
            if (!File.Exists(file) || Hash(await File.ReadAllBytesAsync(file, token)) != expected)
                throw new StudioXException("AG32_SYSTEM_MODIFIED", "系统生成文件已改变或缺失，需要保留修改：" + relative);
        }
        return known.ToArray();
    }

    public static string RenderDeviceForMigration(BuildPlan plan) => CMakeGenerator.RenderDevice(plan) + "\n" + PeripheralCMake + "\n";

    public static bool IsManagedFile(string path, string text) =>
        ((path.Equals(HeaderPath, StringComparison.OrdinalIgnoreCase) || path.Equals(SourcePath, StringComparison.OrdinalIgnoreCase) ||
          path.Equals("device/studiox/StudioX_Board.h", StringComparison.OrdinalIgnoreCase)) && text.StartsWith(Marker, StringComparison.Ordinal)) ||
        (path.StartsWith(Ag32PeripheralSupport.VendorPath, StringComparison.OrdinalIgnoreCase) &&
         Ag32PeripheralSupport.Drivers().TryGetValue(path, out var raw) && Encoding.UTF8.GetString(raw) == text);

    internal static bool IsValidName(string name) =>
        Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,47}\z", RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(name, @"\A(?:GPIO|SYS|INT|BOARD|STUDIOX|StudioX|Delay_|APB_|AHB_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !("auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false class public private protected virtual new delete template this namespace using nullptr main".Split(' ').Contains(name, StringComparer.Ordinal));

    internal static Dictionary<string, byte[]> Render(byte[] ve, string? vendorHeader, IReadOnlyList<Ag32PinFunction> functions)
    {
        var document = new Ag32PinPlanDocument(ve);
        var conflicts = Ag32PinPlanConflicts.Find(document.Assignments, functions);
        if (conflicts.Length != 0) { throw new StudioXException("AG32_PIN_PLAN_CONFLICT", string.Join("\n", conflicts.Select(item => item.Message))); }
        var configured = document.Clocks.SysMhz is not null;
        if (document.Clocks.BusMhz is not null && !configured ||
            Regex.IsMatch(Encoding.UTF8.GetString(ve), @"(?m)^\s*(?:SYSCLK|HSECLK|BUSCLK)\s*,", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "系统代码需要用空格分隔的时钟字段；设置 BUSCLK 时请同时明确 HSECLK 和 SYSCLK。");
        }
        if (configured && (document.Clocks.HseMhz is null || vendorHeader is null))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "生成系统初始化时，请同时填写实际 HSECLK 和 SYSCLK，再保存并生成。");
        }
        uint Frequency(string name, uint fallback)
        {
            if (vendorHeader is null) { return fallback; }
            var match = Regex.Match(vendorHeader, @"(?m)^#define BOARD_" + name + @"_FREQUENCY\s+([0-9]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value) || value == 0 || value > 248_000_000)
            {
                throw new StudioXException("AG32_SYSTEM_CLOCK", "厂商转换结果缺少有效频率：" + name);
            }
            return value;
        }
        var hsi = Frequency("HSI", 10_000_000);
        var hse = Frequency("HSE", 8_000_000);
        var sys = configured ? Frequency("PLL", 0) : hsi;
        var pll = Frequency("PLL", 100_000_000);
        var bus = configured ? Frequency("BUS", 0) : hsi;
        if (configured && (sys % bus != 0 || sys / bus > 256 ||
            !Regex.IsMatch(vendorHeader!, @"(?m)^#define BOARD_PLL_CLKIN\s+PIN_HSE\s*$", RegexOptions.CultureInvariant)))
        {
            throw new StudioXException("AG32_SYSTEM_CLOCK", "当前自动系统初始化仅支持已校验的 HSE→PLL 和整数总线分频；此时钟配置不能自动生成。");
        }
        var config = $"#define STUDIOX_HSI_HZ {hsi}u\n#define STUDIOX_HSE_HZ {hse}u\n#define STUDIOX_SYSCLK_HZ {sys}u\n#define STUDIOX_BUSCLK_HZ {bus}u\n#define STUDIOX_CONFIGURE_PLL {(configured ? 1 : 0)}\n";
        var analog = Ag32PeripheralSupport.Read(ve);
        config += $"#define STUDIOX_PLL_HZ {pll}u\n";
        config += $"#define STUDIOX_ANALOG_SEPARATE_BUS {(document.Clocks.BusMhz is not null ? 1 : 0)}\n#define STUDIOX_ANALOG_BUS_HZ {Frequency("BUS", hsi)}u\n";
        config += $"#define STUDIOX_ANALOG_ENABLED {(analog.Enabled ? 1 : 0)}\n#define STUDIOX_ADC_CHANNEL_MASK 0x{analog.AdcChannels:X4}u\n#define STUDIOX_DAC_MASK {((analog.Dac0 ? 1 : 0) | (analog.Dac1 ? 2 : 0))}u\n#define STUDIOX_CMP_ENABLED {(analog.Comparator ? 1 : 0)}\n";
        var pins = new StringBuilder();
        var objects = new StringBuilder();
        var init = new StringBuilder();
        var enabledPeripherals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in document.Assignments)
        {
            // 只打开已分配外设的时钟；不启动收发、不替用户决定波特率或启动看门狗。
            var peripheral = Regex.Match(pin.Function, @"\A(UART[0-4]|SPI[01]|I2C[01]|CAN0|GPTIMER[0-4]|MAC0|USB0)_", RegexOptions.CultureInvariant);
            if (peripheral.Success && enabledPeripherals.Add(peripheral.Groups[1].Value))
            {
                var module = peripheral.Groups[1].Value;
                var clockBus = module is "MAC0" or "USB0" ? "AHB" : "APB";
                init.AppendLine($"    SYS_Enable{clockBus}Clock({clockBus}_MASK_{module});");
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
                if (direct) { pins.AppendLine($"/* 电气配置：{Ag32GpioElectrical.Describe(pin)}，由映射镜像生效。 */"); }
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
                init.AppendLine($"    /* PIN_{pin.PinNumber}：{Ag32GpioElectrical.Describe(pin)}；电气属性由映射镜像配置。 */");
                init.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{port});\n    GPIO_SetSoftwareMode({port}, {bit});");
                if (pin.Direction == "OUTPUT") { init.AppendLine($"    GPIO_SetLow({port}, {bit});"); }
                init.AppendLine($"    GPIO_Set{(pin.Direction == "OUTPUT" ? "Output" : "Input")}({port}, {bit});");
            }
            else if (!direct && gpio.Success)
            {
                // 外设复用需切到硬件模式；只改本次已映射位，不误启用同组未分配 IO。
                var port = "GPIO" + gpio.Groups[1].Value;
                var bit = "GPIO_BIT" + gpio.Groups[2].Value;
                init.AppendLine($"    SYS_EnableAPBClock(APB_MASK_{port});\n    GPIO_SetHardwareMode({port}, {bit});");
            }
        }
        var result = Ag32PeripheralSupport.Drivers();
        result[HeaderPath] = Encoding.UTF8.GetBytes(Resource("h").Replace("@@CONFIG@@", config).Replace("@@PINS@@", pins.ToString()));
        result[SourcePath] = Encoding.UTF8.GetBytes(Resource("c").Replace("@@PIN_OBJECTS@@", objects.ToString()).Replace("@@PIN_INIT@@", init.ToString()));
        result["device/studiox/StudioX_Board.h"] = Encoding.UTF8.GetBytes(Marker + " Generated clock metadata. */\n#pragma once\n" +
            $"#define BOARD_HSI_FREQUENCY {hsi}u\n#define BOARD_HSE_FREQUENCY {hse}u\n#define BOARD_PLL_FREQUENCY {pll}u\n#define BOARD_BUS_FREQUENCY {bus}u\n");
        return result;
    }

    internal static async Task WriteAsync(string root, Dictionary<string, byte[]> generated, string vePath, byte[] expectedVe,
        byte[]? newVe, CancellationToken token)
    {
        // 在任何写入之前检查所有受管文件；外部编辑不能被下一次图形保存静默覆盖。
        Ag32PeripheralSupport.VerifySdk(root);
        var statePath = PathBoundary.Resolve(root, StatePath);
        var previous = File.Exists(statePath) ? await JsonStore.ReadAsync<Dictionary<string, string>>(statePath, token) : [];
        var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var (relative, bytes) in generated)
        {
            var path = PathBoundary.Resolve(root, relative);
            var old = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
            if (old is not null && (!previous.TryGetValue(relative, out var hash) || hash != Hash(old)))
            {
                throw new StudioXException("AG32_SYSTEM_MODIFIED", relative + " 已被手动修改或被同名文件占用；请保留修改并移出生成目录后重试。");
            }
            before[relative] = old;
        }
        var updates = new Dictionary<string, byte[]>(generated, StringComparer.Ordinal);
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
                    throw new StudioXException("AG32_SYSTEM_CMAKE", "器件 CMake 不是受管文件，不能接入完整外设驱动。");
                var current = updates.TryGetValue(CMakeGenerator.DeviceListPath, out var updated) ? Encoding.UTF8.GetString(updated) : text;
                updates[CMakeGenerator.DeviceListPath] = Encoding.UTF8.GetBytes(current + "\n" + PeripheralCMake + "\n");
                before[CMakeGenerator.DeviceListPath] = bytes;
            }
        }
        updates[StatePath] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(generated.ToDictionary(item => item.Key, item => Hash(item.Value)), JsonStore.Options);
        before[StatePath] = File.Exists(statePath) ? await File.ReadAllBytesAsync(statePath, token) : null;
        if (newVe is not null) { updates[vePath] = newVe; before[vePath] = expectedVe; }
        var currentVe = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, vePath), token);
        if (!expectedVe.SequenceEqual(currentVe))
        {
            throw new StudioXException("AG32_PIN_PLAN_STALE", "VE 在系统代码生成期间发生变化，请重新读取。");
        }
        var written = new List<string>();
        try
        {
            foreach (var (relative, bytes) in updates)
            {
                token.ThrowIfCancellationRequested();
                var path = PathBoundary.Resolve(root, relative);
                var old = before[relative];
                if (old is not null && old.SequenceEqual(bytes)) { continue; }
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
                if (!File.ReadAllBytes(path).SequenceEqual(updates[relative])) { continue; }
                if (before[relative] is { } old) { await ReplaceAsync(path, old, CancellationToken.None); }
                else { File.Delete(path); }
            }
            throw;
        }
    }

    private static async Task ReplaceAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static string Resource(string extension)
    {
        using var stream = typeof(Ag32SystemSupport).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.Ag32.StudioX_System." + extension)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
