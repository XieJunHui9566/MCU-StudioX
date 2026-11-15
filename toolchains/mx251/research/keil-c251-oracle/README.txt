Keil C251 black-box oracle — 2026-10-27

目的：用公开文档、自己的小型 C 探针和既有固定完整工程，区分寄存器反馈、
链接代码压缩与近/远调用规则。不是反编译 Keil 程序，也不是新的 MX251 发行版。

边界
本目录只包含自写探针及离线运行/审计脚本，不包含 Keil 工具或库。
需要用户已有的合法 C251 安装；不下载或安装全局环境，不修改 IDE 默认工具。
所有输出目录必须是新目录。脚本不会接触串口/USB，也不会覆盖已封存输出。
这是研究命令行；MX251 产品工程继续使用 CMake + Ninja + MX251。

复现（<...> 替换为明确路径）
python run.py --keil <C251目录> --fixed-source <已封存C251 calls源码目录> --output <新目录>
python probes.py --keil <C251目录> --startup <已封存startup.a51> --output <新目录>
python near_boundary.py --keil <C251目录> --output <新目录>

run.py 的 --units 可指定其它固定工程的编译单元，默认 main callers callees。
只收录 SMALL/LARGE、ROM(HUGE) 的完整链接，库分别为 C2SSH/C2SLH。
call-widths 是四种 ROM 模型的编译观察，不拿其结果冒充同布局的完整固件。
REGFILE 先从空文件状态启动，每轮保留输入/输出文件；最多六轮。
同时要求 HEX 地址-字节及寄存器表不再变化才记为收敛；达到轮数上限不算成功。
编译警告完整保留。Keil 返回 1 只有原始输出同时明确 0 ERROR(S) 才接受。

首轮证据
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/keil-c251-oracle-20261027
该目录的 inputs、commands、逐轮 REGFILE、LST、COD、MAP、HEX、SHA 和 analysis.json
是结果依据。analyze.py --research <该研究目录> 可重新核对统计。
源文件逐轮等同原封存源；三工程基线 HEX 地址-字节精确复现。
启用 OBJECTADVANCED 后必须用最终 COD/MAP/HEX，不把编译前 LST 当最终指令。
COD 指令字节逐项与 HEX 核对；公共片段按地址计一次，不重复计入各调用者。

主要结果（完整 HEX 已占用字节，非跨度，非动态周期）
                    O7基线  REGFILE  OA7    OA8    OA9    REGFILE+OA9
calls                 5008     4785   5017   5003   3920           3693
MCU                   3445     3416   3431   3431   3312           3283
protocol             19160    19070  19173  19159  19024          18931
O7 SIZE 与 SPEED 在这三个固定工程上地址-字节完全一致。
新配置未实板运行：没有新增 cycles/correctness 成绩，不能用体积代替性能。
原 MX251 编译器、运行库、产品版本和自动任务状态均未修改。

对 MX251 的下一步约束
1. 优先建立可验证的函数破坏集合，不先做全程序代码抽取。
   当前 wide_emit.c.inc:wcall 仍保存所有跨调用的寄存器活值。
   需要统一寄存器字节视图、A/R11、B/R10、隐式 scratch/flags/MDU 状态。
   被调函数的摘要不足以单独决定保存：实参搬运、返回搬运、栈清理也可能破坏活值。
2. 先受限直接调用、叶函数与现有固定 calls/call_chain/parser_feed/app_run；
   未知外部、汇编、间接目标、递归环、中断和运行库仍采用保守集合。
   再逐步用被验证的调用图传播；不猜测未知目标，不直接信任过期摘要。
3. 摘要绑定函数定义、编译器/ABI/模型/选项/库及目标身份；变化就失效。
   受 CMake/Ninja 管理，重复构建有上限和确定的保守回退，不依赖用户手动重编。
   第一实现可以先生成/审计摘要；没有真实性能收益和双板正确性前不默认启用。
4. 后置安全跳转布局与公共片段抽取。ECALL/ERET 与 LCALL/RET 是配对契约，
   不能只按当前距离缩短调用；取址函数、跨段、ISR 和栈深均必须处理。
   抽取重复段要计入额外调用周期与返回地址栈占用，静态 MOV 少不等于动态 MOV 少。

第一阶段验收门槛
固定完整工程与 22 函数模块同时列出 MOV、PUSH/POP、不同宽度栈访问、
ABI 转换和 spill；无法归因的项目写未测/不可判定。保留失败候选。
未知调用、递归、间接调用、汇编、ISR，以及修改 callee 后重编 caller 的失效路径
必须覆盖。实板仍用两块既有授权样品、既有租约和恢复流程。
本次只是确定有证据的优化方向，不保证把 C251 的收益百分比平移到 MX251。
