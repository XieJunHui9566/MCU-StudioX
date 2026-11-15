永久成本模型回归：V0.0136 / struct_records
=======================================
用户要求：以后每次修改成本模型，都重新回答“新模型是否正确拒绝这个候选？”
本目录属于长期验证源码，不受最近一至两套完整编译器的清理规则影响。
不得随轮次清理、重命名、改写失败标签、删除输入或以新生成的候选覆盖旧方案。
未创建提交；与仓库其他源码一样，需要用户安排正常备份。

为什么必须拒绝
--------------
原固定完整 MCU 工程：函数 442→408 B，静态 MOV 77→68，word spill 访问
20→12，spill 槽 3→2，帧 40→36 B；旧启发成本 668→639，选择器接受。
但 AI8051U 同次配对运行：父版 [2963,2963,2964]、候选 [3030,3030,3030]
tick/8 calls，中位耗时 +2.261%。两份固件各 1152 个结果全部正确。
count=64 的路径：热循环新增 64 MOV，外围少 28，净增 36；热循环的
256 次 word 栈访问一条没少。静态循环深度不能代表实际循环次数。
这是“正确性通过、体积变小、速度退步”的负例，不是错码负例。
禁止用协议工程体积收益、平均数、spill 槽变少等理由豁免。

保存内容与边界
--------------
v0.0136-struct-records/ 包含原完整工程源码、两个冻结的 ASM/LST/REL/HEX、
选择诊断、MIR 活跃性/物理占用、原始 UART 与下载报文、独立参考、计时、
路径核算、补丁、成本模型相关原始源码及许可证。manifest.json 固定每项
SHA-256，registry.json 固定 manifest；provenance.json 指向原封存文件。
不包含整套编译器二进制，不依赖轮次目录即可核查这些证据。
原 V0.0136 封存报告未修改；V0.0137 的私有开发以 V0.0135 为源码起点。
实测为 AI8051U；没有新的 STC32G 运行结论，没有 Flash 回读声明。
CFG 核算只是冻结函数的执行路径计数，不是通用 ISA 模型或实测 cycles。

复核命令（Python 路径由使用者配置，无 IDE 依赖）
------------------------------------------------
python -B toolchains/mx251/validation/CheckCostModelRegressions.py \
  --integrity-only --output <新的证据目录>
python -B toolchains/mx251/validation/cost-model-regressions/v0.0136-struct-records/VerifyEvidence.py

重现旧模型误判：
python -B toolchains/mx251/validation/CheckCostModelRegressions.py \
  --model-source toolchains/mx251/validation/cost-model-regressions/v0.0136-struct-records/historical-model \
  --adapter toolchains/mx251/validation/cost-model-regressions/ReplayV0136.py \
  --output <新的证据目录>
此命令必须返回 1 / failed-model-decision / modelQualified=false。
它核对原模型源码及原编译器诊断，重放当年的选择谓词；不是新模型评分器。
模型变更后仍使用旧适配器会失败，不允许拿历史成本冒充新模型重评。

以后每次成本相关变更的必做项
--------------------------
涉及分配/重着色、spill、调用保存、emitter、调度、分支成本、循环权重、
候选选择或目标成本时，在完整工程验收前运行下面的门槛。
RunAcceptance.py --cost-model-change --cost-model-source <本次模型源码目录>
  --cost-model-adapter <本次生产模型适配器.py> <其余原验收参数>
也可单独用 CheckCostModelRegressions.py 先运行，失败则不能晋级候选。
这是一项强制维护/验收约定，不是自动监视每次文件保存的后台程序。

新适配器契约
------------
入口参数 --request request.json --output response.json；不打开硬件。
request 绑定冻结负例 manifest、完整模型源码文件清单与适配器 SHA-256。
必须实际调用本次生产成本实现，重新评价冻结父版和候选；提供两个完整成本、
比较理由以及 accept/reject。修改了模型表示时先更新并审查适配器。
ReplayV0136.py 仅用于历史误判重放；EvaluateLoopCost.py 编译并执行当前
源码中的 tests/probe-wide-loop-cost.c，直接使用生产 wide_loop_cost.h。
主机 C 编译器由 MX251_HOST_CC 指定（例如现有 GCC），不下载或安装编译器。
该适配器只验证生产选择器新增的逐循环否决条件，不冒充完整的选择器或周期模型。
源码目录必须包含模型及依赖；额外模型二进制/配置也要置入其中参与指纹绑定。
适配器应独立单文件，或将其辅助实现纳入模型输入目录。报告保留 stdout/stderr。
response schema=1，requestSha256 为请求文件哈希，caseId 与请求一致，
evaluatedBothFrozenAlternatives=true，selectionBasis="cost-model"，
decision="reject"，reason 为具体解释，costs.parent/candidate 各含 known=true
和 metrics（有单位含义的非负数值字典）。未知成本、缺项、旧请求、未生成候选、
关闭优化或换了一套更好的新汇编都不能把这个固定负例标绿。
检查器校验绑定和结构；“适配器确实使用生产模型、未硬编码负例答案”仍须源码审查。
不得把函数名、基准 ID、期望答案或本次实测数值作为生产选择器特判。
实测/path 数据可用于验证模型，不能直接在适配器里按已知结果硬写 reject。
任何新方案仍另行接受固定完整工程 size/cycles/correctness 验收；本负例通过
只是必要条件，绝不代替逐工程门槛，也不自动授予安装或 LST 资格。

V0.0137 循环保护的覆盖范围
--------------------------
以最终发射的规范汇编文本计算简单单路径循环的一次迭代，逐循环分别比较
指令数、MOV、间接内存操作、SPX 读写和 PUSH/POP，不用外围收益抵消循环内增加。
不输入基准名称、实测计时或已知循环次数；这些计数不是芯片周期预测。
嵌套循环、多路径、跳转表、循环内调用、未知编码或循环身份不匹配返回 unknown。
生产中 unknown 保留原选择器决策并记录未覆盖；这不代表新模型证明了非退步。
永久负例门槛要求两套成本均已知且生产保护明确否决，unknown 仍不能通过。

调用示例（先按使用者本机路径设置 MX251_HOST_CC）：
python -B toolchains/mx251/validation/CheckCostModelRegressions.py \
  --model-source <当前完整源码的 src/mcs251> \
  --adapter toolchains/mx251/validation/cost-model-regressions/EvaluateLoopCost.py \
  --output <新的证据目录>
源码必须包含上述头文件、wide_cost.h 和实际 C 探针；旧模型或其它模型不能
仅换一个适配器名冒充本模型。每次修改仍需审查生产调用点与适配器的一致性。

V0.0138 分支路径扩展
------------------
在V0.0137接口上扩展非嵌套循环：每个条件位置用标签、该标签内条件序号、
条件码和跳转目标标识；逐路径记录走过/取真的条件集合及是否退出循环。
只有路径身份能够完整对应时才比较各项成本，禁止不同路径互相补偿。
每函数最多32个条件位置、128条路径；超限、嵌套、循环调用、跳转表或
无法对齐的结构仍为unknown。适配器同时保存逐路径成本和生产决策。
循环级最大向量只用于诊断，不再作为多路径准入的比较值。
退出路径只计到离开循环成员集合的边，不包含后续trampoline或函数尾部。
这是CFG计数上界的保守比较，不推断输入可达性、分支概率、循环次数或芯片周期。
历史frame_parser正例需从unknown变为已知接受，同时继续已知拒绝冻结V0.0136。
同一轮重着色搜索的全部候选，须对照缓存的原始分配成本，与既有静态准入
使用同一基准。不得让当前中间候选不断改写准入基准，造成搜索顺序依赖。
