SDK 工程覆盖回归（2026-10-31；候选资格，不代表发布/实板资格）

目的
防止只覆盖小基准而遗漏真实 SDK 的函数保留、后端容量和地址空间行为。
不以 GPIO、UART 等函数名选择优化；名称仅用于解释原始工程证据。

永久规则
1. 未用函数剔除须保留 main、ISR、启动入口、显式导出、取址对象、数据/常量
   中的函数引用、汇编引用和库反向引用；未知引用、元数据不完整或间接调用
   分析不闭合时保守保留。函数及其私有尾部/跳表作为完整所有权单元处理。
2. 变换发生在汇编阶段，经正常重汇编及链接生成 HEX。不得裁剪最终 HEX。
   必须核对最终重定位、常量中的指针字节和机器字节；链接成功不是充分条件。
   不得根据“没有指令引用”删除未知外部 .globl：它可能是强制拉入启动清零/
   初始化模块的链接契约。纯声明保持；只有已证明删除的函数定义可清理对应
   导出/导入。所有符号键按汇编器规则大小写敏感，不合并 _Start 与 _start。
3. 容量扩大必须同时验证 value/node 边界、位集合高位、干涉图和物理别名，
   禁止仅增大数组常数。未知类型/指令继续显式回退；禁止静默破坏私有 ABI。
4. 后端拒绝诊断保留函数、iCode/源位置及容量。按完整工程检查覆盖退步，
   不能把整函数回退后“仍可构建”当成没有性能影响。
5. 默认路径、功能开启路径和安全回退均需验证；至少两个 Target Profile
   的离线生成/链接。实板覆盖单独记录，不能由 ISA 相同或镜像相同推定。
6. 每个完整工程分别列 size、时间窗口与 correctness。Keil 版本、优化等级、
   存储模型及链接配置必须记录。无有限执行窗口时，先定义同等 API/事务窗口；
   UART 主机传输时间、硬件 PWM 周期不能冒充 MCU 执行时间。
7. 数量门槛：各固定完整工程 size 和 time 都 <= 同条件 Keil C251 的 110%；
   缺失数据记为未验证，不能按平均值、大小变小或静态指令数补全。

执行本目录的符号/语法回归
python RunFunctionGcTests.py --engine <候选 FunctionGc.ps1> --output <新的证据目录>
要求 PowerShell 7；路径是显式参数，不依赖 MCU StudioX 服务或安装位置。
此入口执行当前引擎的 81 项符号/语法性质检查，并强制重放 startup-link-roots
中的冻结启动负例；不操作硬件，不替代真实工具链和完整工程验收。
冻结重放必须拒绝错误清理启动声明的历史镜像；当前候选还须另做完整启动闭包核对。
结果绑定执行脚本及生产引擎 SHA-256。输出目录必须全新，原失败证据不可覆盖。

本轮还必须执行的独立层次
- 真实编译器/汇编器/链接器：跨 TU、CODE 函数指针、ISR、显式根、共享尾部、
  opaque ASM、间接调用、缺失元数据回退；两 profile 最终字节证明。
- 完整 SDK 固定集合：GPIO 4、Timer/NVIC 5、UART 3、PWM/SPI/I2C/ADC 各 1。
  原始 16 工程的 main、驱动、宏、源顺序及头文件均冻结；不删除工程功能。
- 函数/容量的通用边界测试及旧固定 acceptance ON/OFF。
- 实际生产成本模型：RunAcceptance.py --cost-model-change --cost-model-source ...
  --cost-model-adapter ...，明确拒绝永久 V0.0136 负例，保住 V0.0135 正例。

历史证据索引（相对仓库根）
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-sdk-coverage-20261031/
input-identity.json / sdk-census-final.json / off-differential/results.json
tests/capacity/summary.json / replays/gc-real-bits/results.json
linkproof-evidence/ / hardware-plan-audit.json / progress.json
该索引是已有证据的位置，不是运行本目录回归的隐式工具依赖。
旧 OFF 黄金存在父版同样出现的漂移；不得改黄金掩盖，应保留失败并独立做父候差分。

保护范围
本目录源夹具、执行器、失败证据、SDK 冻结输入清单和必要引用不得作为普通
latest 构建产物清理。清理历史编译器时先保证证据及源码可重建。
