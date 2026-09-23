# AG32 调试配置来源

适用：AG32VF303CCT6、AGM DAP-LINK/USB BLASTER 的 DAP 模式。

- 厂商说明：https://www.ag32mcu.com/dev-docs/doc_ag32_vscode_start/ （官方 DAP 下载、调试配置）
- 本机来源：AgRV_pio-1.8.10-win64-release，`platforms/AgRV/etc/agrv2k.cfg`。
- 原文件 SHA-256：`EA38E3471C32D67E9B52D8C4E182B1B350DAC2107C5C0CCECBF8E9C9DEA08EE8`。
- 平台 `platform.json` 声明版本 1.0.0，许可证 Apache-2.0。
- 配套工具集：`agm.agrv/1.0.0`，AgRV GCC/GDB 11.1.0；AGM OpenOCD 0.12.0+dev-04519-ga93c217e2-dirty。工具随 IDE 提供，不装入本包。

`ag32vf303.cfg` 从官方配置提取 DAP / RISC-V 桥、内存访问和 Flash 驱动定义，固定为此型号。StudioX 添加芯片 ID、Flash 容量、读保护、逻辑区边界检查，以及 GDB 连接前保存、退出时恢复调试控制寄存器并确认 CPU 已运行的处理。没有移植官方脚本中的自动时钟切换和 ROM 下载入口。

当前仅接受已核实的 256 KiB Flash / 128 KiB RAM、应用前 156 KiB / 逻辑末尾 100 KiB 布局：选项字节 0x81000030 的逻辑起始为 0x80027000，反码为 0x7FFD8FFF。其他布局、压缩逻辑、读保护状态会拒绝连接；本配置不解除保护、不更改选项字节、不更新逻辑区。

调试先比较板上应用与本地 ELF，匹配后才进入；调试本身不自动写入固件。下载由用户单独操作，限制在应用区域。此版本的配置解析、工程编译、符号读取和退出处理已设计离线检查；实际硬件验收状态以项目 `docs/AG32-DEBUG-ACCEPTANCE-20260922.md` 为准。
