# AG32VH HyperRAM 控制器

StudioX 的两款 VH 封装共用 8 MiB 控制器。引脚转换和物理布局按具体目标分别运行。控制器接收 32 位 AHB 事务，以 8 位 DDR 接口访问封装内的存储器。源文件来自 [AgRV 平台的 HyperRAM 工程](https://github.com/tianrking/AGM_MCU/tree/42384ae526749db62e346faf689bdede4efbe936/examples/HyperRAM)。封装与板外 RWDS 连线见 [厂商 VH 应用指南](https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_AG32VH_HyperRAM.pdf)。

仿真时从厂商 SDK 准备 `hram_ip.v` 与 `ahb2hbus.v`。对使用无位宽常量的参考版本，将 `ahb2hbus.v` 中两处拼接表达式内的 DMA 地址自增加数写为 `1'b1`，供 Icarus 明确表达式位宽。发布源码保留测试台和集成代码，厂商 RTL 与派生控制器网表由本机提供。

已验证的 `hram_ip.vx` 由这两个输入经过 Quartus Prime Standard 18.1 和 Supra 2026.03.b0 生成。打包时向厂商 IP 流程提供 12 个专用焊盘的 VEX 映射，在 Quartus 将这些端口设为物理引脚，其余顶层端口保持虚拟。关闭复位网的全局时钟自动提升，为读选通留出全局时钟资源。该网表的 SHA-256 为 `44AE01480173982D20BC9C692A96FFAD93F615048A84F45F28EB670837C3E66C`。

`tb.sv` 的存储器模型在封装接口上接收命令、地址和写数据，再经 RWDS 回送收到的数据。测试检查复位、CR0 的首次半字写入、完整 DDR 写事务和 AHB 读回。以下命令在包含这三个文件的目录中运行，`altera_mf.v` 使用已安装 Quartus 的 `quartus/eda/sim_lib` 版本。将 `FIXED` 设为 0 可以复验可变延迟。每次运行会在当前目录生成 `hyperram.vcd`。

```text
iverilog -g2012 -s tb -Ptb.FIXED=1 -o hyperram.vvp -l <altera_mf.v> tb.sv hram_ip.v ahb2hbus.v
vvp hyperram.vvp
```
