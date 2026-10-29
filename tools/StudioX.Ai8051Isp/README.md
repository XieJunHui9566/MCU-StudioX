# AI8051U HID ISP 脚本

独立 C#/.NET 10 命令行工具与 PowerShell 入口。使用 Windows 系统 HID 驱动，无 Python、Node、STC-ISP 窗口或额外 USB 驱动依赖。协议、已验证范围和实验记录见 [详细说明](../../docs/AI8051U-ISP.md)。IDE 的 MX251 下载现使用同一份 C# 核心，入口见 [MX251 说明](../../docs/MX251.md)。

目前实测范围：AI8051U-34K64 / BSL 7.4.1U，板载 AI8H2K12G 下载桥 `VID 34BF / PID FF0F`、接口 0。CPU 为 32 位，HEX 地址为 `FF0000..FFFFFF`。配置文件固定32位，`ircHz`可填1000000–48000000（分频后的系统频率）；默认范例为40MHz。校准误差须在0.5%内，必要时自动分频，下载后读取核对；8 位模式和芯片原生 USB 尚未开放。

先关闭官方 ISP 和占用该板的串口工具，在仓库根目录执行：

```powershell
$isp = '.\tools\StudioX.Ai8051Isp\Invoke-Ai8051Isp.ps1'

# 枚举下载桥；不发送协议命令。
& $isp -Command list

# 检查 HEX、地址范围和哈希；不连接硬件。
& $isp -Command plan -Hex '.\firmware.hex' -NoBuild

# 读取芯片与现有配置。会进入 ISP 并复位运行，不擦写代码。
& $isp -Command inspect -NoBuild

# 下载并保留现有硬件配置。
& $isp -Command flash -Hex '.\firmware.hex' -NoBuild

# 下载指定固件，同时校准并保存 32 位 / 40 MHz 配置。
# 配置写入必须有固件，禁止单独写选项。
& $isp -Command configure -Hex '.\firmware.hex' `
    -Config '.\tools\StudioX.Ai8051Isp\profiles\ai8051u-34k64-32bit-40mhz.json' -NoBuild
```

首次调用默认构建；之后源码未变时可使用 `-NoBuild`。工具不修改系统 PATH。通过 `-Dotnet` 指定受管理的 .NET 10 主机；`-Device` 接受 `list` 返回的完整 HID 路径，多个桥接器时必须明确选择。`-Sha256` 可传入 `plan` 的哈希；未指定时包装脚本先计算，再由核心重新核对。

默认日志写入当前目录 `.build/ai8051u-isp/`，也可用 `-Log` 指定新的文件路径。已有日志拒绝覆盖。标准输出为 JSON 行，标准错误保留原始异常，退出码 0 表示命令完成，1 表示失败，130 表示用户取消。下载成功只表示全部写块收到应答；没有 Flash 读回。`configure` 还要求配置重新读取一致。运行自测是独立验证，不由下载器冒充完成。

协议擦除会覆盖当前程序，并可能按当前硬件选项擦除 EEPROM。中途失败不会自动重试写入或启动不完整镜像。未知硬件选项仍拒绝覆盖；无法校准目标时在擦写前失败。修改IRC不会同步改写应用中的延时或串口参数。

离线检查：

```powershell
dotnet build tools/StudioX.Ai8051Isp -c Release
& './tools/StudioX.Ai8051Isp/bin/Release/net10.0/StudioX.Ai8051Isp.exe' selftest
```

核心可直接由其他进程启动，参数示例：

```text
StudioX.Ai8051Isp.exe flash --hex <HEX> --sha256 <SHA256> --log <新日志路径>
StudioX.Ai8051Isp.exe configure --hex <HEX> --sha256 <SHA256> --config <JSON> --log <新日志路径>
```

工程配置只保存型号、配置组合和固件相对位置；HID 路径属于本机设备选择，工具路径属于用户或工具锁。不要把本机绝对路径写进可共享工程。
