namespace StudioX.Engine;

using System.Text;

/// <summary>一个图形外设的工程源码、公共入口和 CMake 登记共同生成。</summary>
internal sealed record Ag32PeripheralModule(string Name, string InitPurpose)
{
    internal static readonly Ag32PeripheralModule Gpio = new("GPIO", "设置已分配引脚的方向和输出初始电平。");
    internal static readonly Ag32PeripheralModule Uart = new("UART", "配置串口引脚、收发方向与帧格式。");
    internal static readonly Ag32PeripheralModule Timer = new("TIM", "配置时基与通道，并启动已启用的定时器。");
    internal static readonly Ag32PeripheralModule Rtc = new("RTC", "配置 RTC 时钟源和预分频，保留时间与备份寄存器。");
    internal static readonly Ag32PeripheralModule Watchdog = new("WDOG", "按配置的复位超时启动看门狗。");
    internal static readonly Ag32PeripheralModule[] All = [Gpio, Uart,
        new("SPI", "配置 SPI 主机的引脚、串行时钟和字节序。"),
        new("I2C", "配置 I²C 主机引脚，按外设时钟计算 SCL 分频。"),
        new("CAN", "设置 CAN 位时序和接收过滤器，完成后进入正常模式。"), Timer, Rtc,
        new("HYPERRAM", "在首次访问存储器前写入 CR0，设置 7 周期初始延迟及所选延迟模式。"),
        new("FLASHIRQ","安装 Flash 完成与错误回调。"), new("FCBIRQ","安装逻辑配置桥错误回调。"),
        new("CRC","按所选宽度、多项式和种子初始化校验引擎。"), new("BKP","保留所选备份寄存器中的已有数据。"),
        new("ANALOG", "配置模拟转换序列、输出与比较器输入。"), new("EXTIRQ", "安装外部与内核直连中断。"),
        new("DMA", "使能 DMA 控制器，传输在应用提供缓冲区后启动。"), new("POWER", "配置低功耗唤醒源。"),
        new("USB", "配置全速 USB 控制器与队列。"),
        new("ETH", "配置以太网 MAC、描述符和外部 PHY。"), Watchdog,
        new("IWDG", "按所选时钟和预分频启动独立看门狗。")];
    internal string Header => $"include/{Name}.h";
    internal string LegacyHeader => $"src/{Name}.h";
    internal string Source => $"src/{Name}.c";
    internal string Init => $"SX_{Name}_Init";
    internal string CMake => "target_sources(studiox_device INTERFACE \"${CMAKE_CURRENT_LIST_DIR}/../" + Source + "\")";

    internal void Render(Dictionary<string, byte[]> files, string declarations = "", string definitions = "", string initialization = "")
    {
        // Windows 上 UART.h 与 uart.h 同名，公共头文件也向原有调用者提供原厂声明。
        files[Header] = Encoding.UTF8.GetBytes($$"""
            {{Ag32SystemSupport.Marker}} */
            #pragma once
            #include "alta.h"

            #ifdef __cplusplus
            extern "C" {
            #endif
            /* 在 main 中、首次使用相应外设前调用。 */
            void {{Init}}(void);
            {{declarations.TrimEnd()}}
            #ifdef __cplusplus
            }
            #endif
            """ + "\n");
        var callbackNote = !definitions.Contains("__attribute__((weak))", StringComparison.Ordinal) ? "" : " 应用回调请在自己的 .c 文件中按相同签名实现。";
        files[Source] = Encoding.UTF8.GetBytes($$"""
            {{Ag32SystemSupport.Marker}}{{callbackNote}} */
            #include "{{Name}}.h"
            #include "StudioX_System.h"

            {{definitions.TrimEnd()}}

            /* {{(initialization.Length == 0 ? "当前配置已停用此模块，保留空初始化入口。" : InitPurpose)}} */
            void {{Init}}(void)
            {
            {{initialization.TrimEnd()}}
            }
            """ + "\n");
    }
}
