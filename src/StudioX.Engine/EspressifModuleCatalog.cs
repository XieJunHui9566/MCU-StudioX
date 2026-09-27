namespace StudioX.Engine;

/// <summary>按官方完整料号提供模组规格；板卡尚未声明的外置存储不会由芯片名称推算。</summary>
public static class EspressifModuleCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EspressifModuleProfile>> Profiles = CreateProfiles()
        .GroupBy(profile => profile.Target, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<EspressifModuleProfile>)Array.AsReadOnly(group.ToArray()), StringComparer.Ordinal);

    public static IReadOnlyList<EspressifModuleProfile> ForTarget(string target) =>
        Profiles.TryGetValue(target, out var profiles) ? profiles : Array.Empty<EspressifModuleProfile>();

    private static List<EspressifModuleProfile> CreateProfiles()
    {
        var profiles = new List<EspressifModuleProfile>();
        AddEsp32(profiles);
        AddEsp32S3(profiles);
        AddEsp32P4(profiles);
        AddEsp32C3(profiles);
        AddEsp32C5(profiles);
        AddEsp32C6(profiles);
        AddEsp8266(profiles);
        return profiles;
    }

    private static void AddEsp32(List<EspressifModuleProfile> profiles)
    {
        const string wroom = "https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32_datasheet_en.pdf";
        const string wroomDu = "https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32d_esp32-wroom-32u_datasheet_en.pdf";
        const string wroomEu = "https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32e_esp32-wroom-32ue_datasheet_en.pdf";
        const string wroverEi = "https://www.espressif.com/sites/default/files/documentation/esp32-wrover-e_esp32-wrover-ie_datasheet_en.pdf";
        const string wroverBi = "https://www.espressif.com/sites/default/files/documentation/esp32-wrover-b_datasheet_en.pdf";
        const string mini = "https://www.espressif.com/sites/default/files/documentation/esp32-mini-1_datasheet_en.pdf";
        const string solo = "https://www.espressif.com/sites/default/files/documentation/esp32-solo-1_datasheet_en.pdf";
        Add(profiles, "esp32", "ESP32-WROOM-32", 4, "qio", 40, "disabled", null,
            "数据手册 v3.8；双核，PCB 天线；NRND。", wroom, singleCore: false);
        Add(profiles, "esp32", "ESP32-WROOM-32D", 4, "qio", 40, "disabled", null,
            "数据手册 v2.8；双核，PCB 天线；NRND。", wroomDu, singleCore: false);
        Add(profiles, "esp32", "ESP32-WROOM-32U", 4, "qio", 40, "disabled", null,
            "数据手册 v2.8；双核，外接天线连接器；NRND。", wroomDu, singleCore: false);

        foreach (var family in new[] { "ESP32-WROOM-32E", "ESP32-WROOM-32UE" })
        {
            var antenna = family.EndsWith("UE", StringComparison.Ordinal) ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32", family, wroomEu, $"数据手册 v2.1 表 1；双核，{antenna}。", 40,
                [new("N4", 4), new("N8", 8), new("N16", 16), new("H4", 4, Note: "105 °C 等级"),
                    new("H8", 8, Note: "105 °C 等级"), new("N4R2", 4, 2), new("N8R2", 8, 2), new("N16R2", 16, 2)]);
        }
        foreach (var family in new[] { "ESP32-WROVER-E", "ESP32-WROVER-IE" })
        {
            var antenna = family.EndsWith("IE", StringComparison.Ordinal) ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32", family, wroverEi,
                $"数据手册 v2.4 表 1；双核，{antenna}；存储总线 3.3 V。8 MiB PSRAM 的普通映射窗口最多 4 MiB。", 40,
                [new("N4R8", 4, 8), new("N8R8", 8, 8), new("N16R8", 16, 8),
                    new("N4R2", 4, 2, Note: "此 2 MiB PSRAM 料号已 EOL"),
                    new("N8R2", 8, 2, Note: "此 2 MiB PSRAM 料号已 EOL"),
                    new("N16R2", 16, 2, Note: "此 2 MiB PSRAM 料号已 EOL")]);
        }
        foreach (var family in new[] { "ESP32-WROVER-B", "ESP32-WROVER-IB" })
        {
            var antenna = family.EndsWith("IB", StringComparison.Ordinal) ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32", family, wroverBi,
                $"数据手册 v2.4 表 1；双核，{antenna}；NRND。8 MiB PSRAM 的普通映射窗口最多 4 MiB。", 40,
                [new("N4R8", 4, 8), new("N8R8", 8, 8), new("N16R8", 16, 8)]);
        }
        foreach (var family in new[] { "ESP32-MINI-1", "ESP32-MINI-1U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32", family, mini, $"数据手册 v1.8；ESP32-U4WDH 双核，封装内 Flash，{antenna}。", 40,
                [new("N4", 4), new("H4", 4, Note: "105 °C 等级")]);
        }
        foreach (var suffix in new[] { "N4", "H4" })
        {
            Add(profiles, "esp32", $"ESP32-SOLO-1-{suffix}", 4, "qio", 40, "disabled", null,
                $"数据手册 v2.4 表 1；ESP32-S0WD 单核，PCB 天线；NRND。{(suffix == "H4" ? "105 °C 等级。" : string.Empty)}",
                solo, singleCore: true);
        }
    }

    private static void AddEsp32S3(List<EspressifModuleProfile> profiles)
    {
        const string wroom1 = "https://documentation.espressif.com/esp32-s3-wroom-1_wroom-1u_datasheet_en.html";
        const string wroom2 = "https://documentation.espressif.com/esp32-s3-wroom-2_datasheet_en.html";
        const string mini = "https://documentation.espressif.com/esp32-s3-mini-1_mini-1u_datasheet_en.html";
        // 两个 WROOM-1 天线版本的官方表均列出这些料号；R8 表示 PSRAM，不表示 Flash 也是 Octal。
        foreach (var family in new[] { "ESP32-S3-WROOM-1", "ESP32-S3-WROOM-1U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32s3", family, wroom1, $"数据手册 v1.8 表 1-1/1-2；{antenna}；存储总线 3.3 V。", 80,
                [new("N4", 4), new("N8", 8), new("N16", 16), new("H4", 4, Note: "105 °C 等级"),
                    new("N4R2", 4, 2), new("N8R2", 8, 2), new("N16R2", 16, 2),
                    new("N4R8", 4, 8, "octal"), new("N8R8", 8, 8, "octal"), new("N16R8", 16, 8, "octal")]);
            Add(profiles, "esp32s3", $"{family}-N16R16VA", 16, "qio", 80, "octal", 16,
                $"数据手册 v1.8 表 1-1/1-2；{antenna}；Quad Flash + Octal PSRAM，存储总线 1.8 V。电压不由 IDE 改写。", wroom1);
        }
        Add(profiles, "esp32s3", "ESP32-S3-WROOM-2-N16R8V", 16, "opi", 80, "octal", 8,
            "数据手册 v1.7 表 1-1；Octal Flash + Octal PSRAM，1.8 V；PCB 天线；EOL。GPIO47/48 亦为 1.8 V。", wroom2);
        Add(profiles, "esp32s3", "ESP32-S3-WROOM-2-N32R8V", 32, "opi", 80, "octal", 8,
            "数据手册 v1.7 表 1-1；Octal Flash + Octal PSRAM，1.8 V；PCB 天线；EOL。GPIO47/48 亦为 1.8 V。", wroom2);
        Add(profiles, "esp32s3", "ESP32-S3-WROOM-2-N32R16V", 32, "opi", 80, "octal", 16,
            "数据手册 v1.7 表 1-1；Octal Flash + Octal PSRAM，1.8 V；PCB 天线。GPIO47/48 亦为 1.8 V。", wroom2);
        foreach (var family in new[] { "ESP32-S3-MINI-1", "ESP32-S3-MINI-1U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32s3", family, mini, $"数据手册 v1.7 表 1-1；封装内存储，{antenna}。", 80,
                [new("N8", 8), new("N4R2", 4, 2)]);
        }
    }

    private static void AddEsp32P4(List<EspressifModuleProfile> profiles)
    {
        const string current = "https://www.espressif.com/sites/default/files/documentation/esp32-p4_datasheet_en.pdf";
        const string legacy = "https://documentation.espressif.com/esp32-p4-chip-revision-v1.3_datasheet_en.pdf";
        foreach (var size in new[] { 16, 32 })
        {
            // NRW 标识封装内 PSRAM，不声明板卡焊接的外部 Flash；两种修订使用不同的 SDK 二进制。
            Add(profiles, "esp32p4", $"ESP32-P4NRW{size}X", null, null, null, "hex", size,
                "数据手册 v0.7 表 1-1；v3.x 修订，QFN104；封装内 16 线 PSRAM，1.8 V。外置 Flash 规格沿用板卡配置。", current,
                revision: "current");
            Add(profiles, "esp32p4", $"ESP32-P4NRW{size}", null, null, null, "hex", size,
                "v1.3 修订专用数据手册 v1.2；旧修订，EOL，QFN104；封装内 16 线 PSRAM，1.8 V。外置 Flash 规格沿用板卡配置。", legacy,
                revision: "legacy");
        }
    }

    private static void AddEsp32C3(List<EspressifModuleProfile> profiles)
    {
        const string wroom = "https://documentation.espressif.com/esp32-c3-wroom-02_datasheet_en.html";
        const string mini = "https://documentation.espressif.com/esp32-c3-mini-1_datasheet_en.html";
        foreach (var family in new[] { "ESP32-C3-WROOM-02", "ESP32-C3-WROOM-02U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32c3", family, wroom, $"数据手册 v1.7 表 1-1/1-2；{antenna}。", 80,
                [new("N4", 4), new("H4", 4, Note: "105 °C 等级"), new("N8", 8)]);
        }
        AddVariants(profiles, "esp32c3", "ESP32-C3-MINI-1", mini, "数据手册 v2.2；封装内 Flash，PCB 天线。", 80,
            [new("N4X", 4, Note: "芯片修订 v1.1"), new("H4X", 4, Note: "芯片修订 v1.1，105 °C 等级"),
                new("H8X", 8, Note: "芯片修订 v1.1，105 °C 等级"), new("N4", 4, Note: "旧修订 v0.4，NRND"),
                new("H4", 4, Note: "旧修订 v0.4，105 °C 等级，NRND"), new("H4-AZ", 4, Note: "旧修订 v0.4，105 °C 等级，NRND")]);
        AddVariants(profiles, "esp32c3", "ESP32-C3-MINI-1U", mini, "数据手册 v2.2；封装内 Flash，外接天线连接器。", 80,
            [new("N4X", 4, Note: "芯片修订 v1.1"), new("H4X", 4, Note: "芯片修订 v1.1，105 °C 等级"),
                new("N4", 4, Note: "旧修订 v0.4，NRND"), new("H4", 4, Note: "旧修订 v0.4，105 °C 等级，NRND")]);
    }

    private static void AddEsp32C5(List<EspressifModuleProfile> profiles)
    {
        const string source = "https://documentation.espressif.com/esp32-c5-wroom-1_wroom-1u_datasheet_en.html";
        AddVariants(profiles, "esp32c5", "ESP32-C5-WROOM-1", source, "数据手册 v1.3 表 1-1；PCB 天线。", 80,
            [new("N4", 4), new("N4R2", 4, 2), new("N8R2", 8, 2), new("N16R2", 16, 2),
                new("N8R8", 8, 8), new("N16R8", 16, 8), new("N32R8", 32, 8, Note: "超过 16 MiB 的 Flash 缓存访问需要 SDK 的独立实验选项")]);
        // 1U 的正式表没有 N4R2、N8R2，不能按另一种天线版本补全后缀。
        AddVariants(profiles, "esp32c5", "ESP32-C5-WROOM-1U", source, "数据手册 v1.3 表 1-2；外接天线连接器 ANT1。", 80,
            [new("N4", 4), new("N16R2", 16, 2), new("N8R8", 8, 8), new("N16R8", 16, 8),
                new("N32R8", 32, 8, Note: "超过 16 MiB 的 Flash 缓存访问需要 SDK 的独立实验选项")]);
        Add(profiles, "esp32c5", "ESP32-C5-WROOM-1U-N8R8T2", 8, "qio", 80, "quad", 8,
            "数据手册 v1.3 的明确订货示例；外接天线连接器 ANT2 定制，存储规格与 N8R8 相同。", source);
    }

    private static void AddEsp32C6(List<EspressifModuleProfile> profiles)
    {
        const string wroom = "https://documentation.espressif.com/esp32-c6-wroom-1_wroom-1u_datasheet_en.html";
        const string mini = "https://documentation.espressif.com/esp32-c6-mini-1_mini-1u_datasheet_en.html";
        foreach (var family in new[] { "ESP32-C6-WROOM-1", "ESP32-C6-WROOM-1U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32c6", family, wroom, $"数据手册 v1.4 表 1-1/1-2；{antenna}。", 80,
                [new("N4", 4), new("N8", 8), new("N16", 16)]);
        }
        foreach (var family in new[] { "ESP32-C6-MINI-1", "ESP32-C6-MINI-1U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线连接器" : "PCB 天线";
            AddVariants(profiles, "esp32c6", family, mini, $"数据手册 v1.5；封装内 Flash，{antenna}。", 80,
                [new("N4", 4), new("H4", 4, Note: "105 °C 等级"), new("H8", 8, Note: "105 °C 等级")]);
        }
    }

    private static void AddEsp8266(List<EspressifModuleProfile> profiles)
    {
        const string source = "https://documentation.espressif.com/esp-wroom-02u_esp-wroom-02d_datasheet_en.html";
        foreach (var family in new[] { "ESP-WROOM-02D", "ESP-WROOM-02U" })
        {
            var antenna = family.EndsWith('U') ? "外接天线 U.FL 连接器" : "PCB 天线";
            // 保守的 DIO / 40 MHz 可用于这些模块，不根据第三方 ESP-12 或 NodeMCU 名字推断容量。
            AddVariants(profiles, "esp8266", family, source, $"数据手册 v2.3 表 1-1/1-2；{antenna}；NRND；无 PSRAM。", 40,
                [new("H2", 2, Note: "105 °C 等级"), new("N2", 2), new("N4", 4), new("N16", 16)], flashMode: "dio");
        }
    }

    private static void AddVariants(List<EspressifModuleProfile> profiles, string target, string family, string source,
        string summary, int flashFrequency, Variant[] variants, string flashMode = "qio")
    {
        foreach (var variant in variants)
        {
            Add(profiles, target, $"{family}-{variant.Suffix}", variant.FlashSize, flashMode, flashFrequency,
                variant.PsramSize is null ? "disabled" : variant.PsramMode, variant.PsramSize,
                string.IsNullOrEmpty(variant.Note) ? summary : $"{summary} {variant.Note}。", source,
                singleCore: target == "esp32" ? false : null);
        }
    }

    private static void Add(List<EspressifModuleProfile> profiles, string target, string label, int? flashSize, string? flashMode,
        int? flashFrequency, string psramMode, int? psramSize, string summary, string source,
        string? revision = null, bool? singleCore = null)
    {
        var id = label.ToLowerInvariant();
        var settings = new EspressifModuleSettings(ProfileId: id, FlashSizeMb: flashSize, FlashMode: flashMode,
            FlashFrequencyMhz: flashFrequency, PsramMode: psramMode, PsramSizeMb: psramSize,
            P4RevisionFamily: revision, SingleCore: singleCore);
        var flash = flashSize is null ? "Flash 依板卡配置" : $"{flashSize} MiB {flashMode} Flash，{flashFrequency} MHz";
        var ram = psramSize is null ? "无 PSRAM" : $"{psramSize} MiB {psramMode} PSRAM（硬件规格）";
        profiles.Add(new(id, label, target, settings, $"{flash}；{ram}。{summary}", source));
    }

    private sealed record Variant(string Suffix, int FlashSize, int? PsramSize = null, string PsramMode = "quad", string? Note = null);
}
