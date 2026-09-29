# ARM32 通用 CMSIS 工程

StudioX Pack 格式 1，按器件子系列独立安装。当前提供 C / CMSIS 基础模板：原厂寄存器头文件、实际向量表、系统文件、按准确型号生成的链接脚本，以及集中在 `StudioX_System.h/.c` 的毫秒时基。主函数保持简洁，不预设开发板 LED 或串口引脚。

首次构建由 IDE 管理的 `arm.gnu/1.0.0` 工具集完成，包内不携带编译器，不修改系统 PATH。来源的版本、下载 URL、SHA-256、完整内存区域和原始向量记录见 `provenance.json`、`vendor/` 与 `licenses/`；头文件自身的许可声明保留不变，厂商资源不统一改为本项目许可。

本包采用厂商系统文件的默认设置。实际板卡若改变晶振或时钟，必须按该器件参考手册调整系统配置并调用 `SystemCoreClockUpdate()`。`StudioX_System_Init()` 配置 1 ms SysTick；自定义 SysTick 或 RTOS 时应一起调整时基。`StudioX_DelayMs()` 只能在线程上下文、中断开启时使用。

这轮属于离线工程与编译支持。没有连接或烧录实板；下载/调试未声明，IDE 不会把未知芯片当作相近型号操作。无线 MCU 的模板不包含 SoftDevice、BLE、Thread 或其他无线协议栈。多核、安全启动、外部 Flash 启动等需要专门工程模型的器件不套用通用模板。

独立主 RAM 区之外的 DTCM、CCM、其他 SRAM、EEPROM、备份区和配置区不自动合并。需扩展时查看包内完整内存证据并维护工程链接脚本，不能按型号字符串猜容量。
