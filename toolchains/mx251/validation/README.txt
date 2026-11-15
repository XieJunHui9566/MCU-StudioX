2026-10-29 永久成本模型门槛：见 cost-model-regressions/README.txt。
修改成本模型时必须以 RunAcceptance.py --cost-model-change --cost-model-source ...
--cost-model-adapter ... 启动验收；先重新评价冻结的 V0.0136 struct_records 父/候选，
正确拒绝失败候选后再运行完整矩阵。旧模型历史重放当前失败，不能标成已修复。
原始证据通过完整性检查不等于模型通过；未知成本、候选消失和关闭优化都不能替代重评。

MX251 V0.0129 development（非 LST、未安装）：完整旧选择器外层比较保守无符号整数调度候选；只改 wide.c，ABI/runtime/startup 不变。
固定 48 项（每模式 18 固件 + 6 预期拒绝）和两模式严格重编通过，36 HEX 与原矩阵逐一相同；14 HEX 变化、18 真实函数实例缩小、无函数体积增加。
STC32G calls/callbacks small/large × OFF/ON 共 8 份、AI fixed-calls/callbacks small/large × OFF/ON 共 6 份，及两板原固件恢复均由独立原始证据审查确认；没有 Flash 回读声明。
pressure 移除 24 条静态 word spill 访问及 6 值槽/24 B 帧；native ON 的 PUSH、POP 各 +4。回调间接路线未证实改善。三样本历史同 HEX 基线与 Timer0 /12 系统时钟计数只支持所测工程路线，不宣称全程序周期或所有成本下降。
保留三项旧检查失败及补审、AI v1 runner 变量覆盖拒绝、STC 初次等待超时及审查假设修正、AI 完整身份首次 D5 拒绝。用户 standing offset14 观察规则继续有效；其余 53 字节、已确认板/bridge/COM6 与配置仍严格，永久身份规则不等于任意固件授权。
独立 CLI/CMake/Ninja 与既有 SDK 契约保持；未改 IDE、产品号、安装版或打包。自动流程仍 PAUSED。

以下为历史记录（保留原字节；历史固件范围不自动扩大）：

MX251 V0.0128：普通调用的栈整数参数批量压栈；开发版、非LST、未安装
仅gen.c为已分配到栈的16/32位整数参数增加受限路径；普通ABI2/4调用者到已解析ABI4直接callee，
保持实际参数字节布局、A/B/flags约定与现有回退，不改ABI、启动、运行库或器件能力。
固定协议main计时窗一处：MOV -1、PUSH -3、POP 0、stack byte access -3，新增2次word读取；
ABI conversion、spill对象/存储减少为0；源数据仍读4字节，新增参数栈顶1字节peek以恢复A。
完整协议OFF 21446→21435 B，ON 21434→21423 B，均仅少11 B（约0.051%）；main窗90→86条、183→172 B。
ON/OFF各18镜像+6预期拒绝严格重建通过；其余34个HEX相同，仅继承父版原有证据范围。
四份协议父/新×OFF/ON经原始数据独立审查；每份512普通结果与9个timed digest通过。
512普通结果使用寄存器seed，不执行新路径；真实新路径覆盖是case0/32/63各3次计时并核对digest。
已恢复约定数学自测，804唯一记录/268项，其中267符合参考，仅历史NaN132差异；租约释放。
恢复写入ACK后1秒排空未收到数据使原采集停止；另租约只读补核闭合，无追加烧录。
补核判断器输出路径保护错误及原失败receipt保留；同字节helper在补采目录纯离线重判通过。
下载ACK、实际回传、Flash回读严格区分，本轮未做Flash回读、配置写入或STC32G写入。
本轮RawStatus偏移14例外来自明确V128授权，其余53字节及身份字段同会话擦写前严格；轮末失效。
三个计时样本不证明稳定加速，静态条数不是CPU cycles；逐工程Keil最低门槛仍未整体达到。
下一主要对象是原固定调用工程m_c_pressure/n_c_pressure：每次调用12次word spill写+12次读，
合计24次/48数据字节，64次调用为1536次访问；本版完全未减少，需要独立寄存器活跃期/分配研究。
死机中断的严格构建日志原样保留，源码与完成矩阵哈希复核后重建中断阶段；离线最终审查通过。
广泛浮点/ISR/full-Flash、large原生数学、跨宿主闭包及当前SDK安装集成仍未完成。
独立MX251 + CMake + Ninja入口不依赖IDE；未改安装工具/产品版本、未提交推送、自动保持PAUSED。
本轮原始计时（12系统时钟/tick；仅三个样本，不是稳定加速结论）：
ai-fixed-protocol-parent-OFF IRC=39943200Hz; case-0: ticks=[34, 34, 34], median=34; case-32: ticks=[88, 88, 88], median=88; case-63: ticks=[7353, 7353, 7353], median=7353
ai-fixed-protocol-candidate-OFF IRC=39943200Hz; case-0: ticks=[34, 34, 34], median=34; case-32: ticks=[87, 87, 87], median=87; case-63: ticks=[7353, 7353, 7352], median=7353
ai-fixed-protocol-parent-ON IRC=39943200Hz; case-0: ticks=[34, 34, 34], median=34; case-32: ticks=[87, 88, 88], median=88; case-63: ticks=[7287, 7287, 7287], median=7287
ai-fixed-protocol-candidate-ON IRC=39943200Hz; case-0: ticks=[34, 35, 34], median=34; case-32: ticks=[87, 88, 88], median=88; case-63: ticks=[7286, 7287, 7287], median=7287

以下为历史记录（原字节保留，授权不自动延续）：

MX251 V0.0127：相邻字段地址复用；开发版、非LST、未安装
仅改变wide MIR相邻同基址、常量偏移的普通字段存储，复用DR16地址并保持每次24bit归一化。
调用、加载、控制流和不支持的别名/volatile形态保留回退；不改ABI、栈布局、启动或运行库。
ON/OFF每模式18镜像+6预期拒绝通过。协议OFF21470→21446B，ON21458→21434B。
ring_init减少3条MOV，parser_init减少7条，parser_feed/app_run各减少1条，合计12条/24B。
PUSH/POP、stack byte access、ABI conversion、spill变化均为0；其余17镜像每模式逐字节不变。
本轮AI协议父版/候选×ON/OFF四行运行均通过512结果，原始传输/回传由独立审查绑定。
已恢复原数学自测，268项中267符合参考，仅历史NaN132差异；租约释放，无配置写和Flash回读。
AI本轮按新的人类明确授权仅观察RawStatus偏移14，其他53字节及桥/串口/型号/BSL/频率严格；
私有CLI在同一写入会话Detect后、擦写前检查。字段含义仍未确认，例外随本轮结束失效。
此前完整身份拒绝、未写入及原数学只接收观察的证据保留，不将其表述为成功烧录。
STC本轮未写入；34个相同HEX映射仅继承V0.0126既有范围，不称本轮重测或扩大资格。
静态指令计数不是cycles；三个计时样本及1tick差异不单独证明稳定性能变化。
协议体积仍大于同源Keil OPT7的19160B，逐工程size/time≤100%最低线尚未整体达到。
main普通ABI参数准备、seed字节栈搬运及寄存器压力仍存在；不以小幅体积收益替代主要瓶颈解决。
有限模型、保留寄存器bank别名和死flags的范围见报告。广泛浮点/ISR/fullFlash、large原生数学、
跨宿主依赖闭包及当前SDK安装集成仍待完成。独立CLI+CMake+Ninja入口保持，IDE仅为适配层。
未改IDE安装/产品版本/系统PATH，未制作发行包，未提交推送，自动任务保持PAUSED。
本轮原始计时（Timer0 12分频，按各IRC换算，非跨工程平均）：
ai-fixed-protocol-baseline-OFF IRC=39943200Hz; case-0: ticks=[35, 35, 35], median=35; case-32: ticks=[89, 88, 88], median=88; case-63: ticks=[7355, 7355, 7356], median=7355
ai-fixed-protocol-candidate-OFF IRC=39943200Hz; case-0: ticks=[34, 34, 34], median=34; case-32: ticks=[88, 88, 88], median=88; case-63: ticks=[7353, 7353, 7353], median=7353
ai-fixed-protocol-baseline-ON IRC=39943200Hz; case-0: ticks=[35, 35, 35], median=35; case-32: ticks=[89, 89, 88], median=89; case-63: ticks=[7289, 7289, 7289], median=7289
ai-fixed-protocol-candidate-ON IRC=39943200Hz; case-0: ticks=[34, 34, 34], median=34; case-32: ticks=[87, 88, 88], median=88; case-63: ticks=[7287, 7287, 7287], median=7287

以下为历史记录（原字节保留，历史授权不自动延续）：

MX251 V0.0126：相邻单次 CODE 索引地址融合，开发版、非 LST、未安装
0019-indexed-code-address.patch 基于 V0.0125，只修改 src/mcs251/gen.c，沿用 GPL。
紧邻且单次使用的 CODE 临时地址使用原字节 ADD/ADDC 直接写 DPL/DPH/DPXL；保留原
MOVC顺序、结果写回及DPX高8位，无新DR scratch；不改ABI、运行库、启动或ordinary准入。
unsigned 8/16-bit 索引及所有原始/物化类型守卫保留；同宽CODE cast可规范化后命中。
atomic两版前端拒绝，不宣称运行回退资格；有限差分模型不等于完整ISA/C或并发证明。
ON/OFF严格构建各18镜像与6预期拒绝通过；两份acceptance保持原candidate标签和原字节。
每模式6镜像变化、12镜像同HEX。36行中24行只继承父版原资格范围，12变化行绑定本轮
2份STC候选和6份AI候选运行；整数ON/OFF仅在完整HEX相同后共享证据。
STC small/large及原整数固件恢复，各6400算术/8初始化/IRQ通过；命令04实际写入，尾16B
与擦除前配置一致。AI共10行（MCU/协议父版候选两模式8行+整数2行）原始oracle通过；
指定数学恢复267/268，仅原NaN132差异。两板租约释放；ACK与运行分别验证，无Flash回读。
首次严格AI检查在写入前拦截，无固件/配置写入，不是恢复失败。用户随后独立授权V126仅
本轮观察byte14；其余53字节和全部身份严格匹配，同会话擦除前复核，轮末到期、不可自动
复用，字段含义未知；不是继承V125例外。私有测试CLI隔离，未进入生产或安装工具。
协议完整ON 21512->21458B、OFF 21524->21470B；main计时段99->90条、210->183B、
MOV47->38；PUSH/POP/SPX宽度/ABI/spill序列不变。静态指令减少不等于STC周期收益。
完整协议仍大于Keil19160B，逐工程size/time<=100%及90.91%目标未全部达到，不用平均值抵消。
独立CLI与CMake+Ninja入口不依赖StudioX服务/安装目录；IDE是适配层，未安装SDK/IDE，
不授LST，不提交推送，不重启旧自动任务。详细原始证据与范围见mx251-v0.0126最终报告。
以下是本次原始三个tick样本的中位记录；稳定性及clock/timer归一解释以独立审核为准：
ai-fixed-mcu baseline OFF：identity ticks=[53, 53, 53] median=53 repeats=8；sum_bytes ticks=[789, 789, 789] median=789 repeats=8；copy_xor ticks=[1734, 1734, 1734] median=1734 repeats=8；ring_buffer ticks=[2255, 2255, 2255] median=2255 repeats=8；state_machine ticks=[2069, 2069, 2069] median=2069 repeats=8；frame_parser ticks=[1299, 1299, 1299] median=1299 repeats=8；struct_records ticks=[2963, 2963, 2964] median=2963 repeats=8；crc16 ticks=[5325, 5325, 5325] median=5325 repeats=8；call_chain ticks=[2796, 2796, 2796] median=2796 repeats=8；IRC=39943200Hz。
ai-fixed-mcu candidate OFF：identity ticks=[53, 53, 53] median=53 repeats=8；sum_bytes ticks=[789, 789, 789] median=789 repeats=8；copy_xor ticks=[1734, 1734, 1734] median=1734 repeats=8；ring_buffer ticks=[2255, 2255, 2255] median=2255 repeats=8；state_machine ticks=[2069, 2069, 2069] median=2069 repeats=8；frame_parser ticks=[1299, 1299, 1299] median=1299 repeats=8；struct_records ticks=[2963, 2963, 2964] median=2963 repeats=8；crc16 ticks=[5325, 5325, 5325] median=5325 repeats=8；call_chain ticks=[2796, 2796, 2796] median=2796 repeats=8；IRC=39943200Hz。
ai-fixed-protocol baseline OFF：case-0 ticks=[37, 37, 37] median=37 repeats=1；case-32 ticks=[90, 90, 90] median=90 repeats=1；case-63 ticks=[7357, 7356, 7357] median=7357 repeats=1；IRC=39943200Hz。
ai-fixed-protocol candidate OFF：case-0 ticks=[35, 35, 35] median=35 repeats=1；case-32 ticks=[89, 88, 88] median=88 repeats=1；case-63 ticks=[7355, 7355, 7356] median=7355 repeats=1；IRC=39943200Hz。
ai-fixed-mcu baseline ON：identity ticks=[53, 53, 53] median=53 repeats=8；sum_bytes ticks=[789, 789, 789] median=789 repeats=8；copy_xor ticks=[1734, 1734, 1734] median=1734 repeats=8；ring_buffer ticks=[2255, 2255, 2255] median=2255 repeats=8；state_machine ticks=[2069, 2069, 2069] median=2069 repeats=8；frame_parser ticks=[1299, 1299, 1299] median=1299 repeats=8；struct_records ticks=[2963, 2963, 2964] median=2963 repeats=8；crc16 ticks=[5325, 5325, 5325] median=5325 repeats=8；call_chain ticks=[2796, 2796, 2796] median=2796 repeats=8；IRC=39943200Hz。
ai-fixed-mcu candidate ON：identity ticks=[53, 53, 53] median=53 repeats=8；sum_bytes ticks=[789, 789, 789] median=789 repeats=8；copy_xor ticks=[1734, 1734, 1734] median=1734 repeats=8；ring_buffer ticks=[2255, 2255, 2255] median=2255 repeats=8；state_machine ticks=[2069, 2069, 2069] median=2069 repeats=8；frame_parser ticks=[1299, 1299, 1299] median=1299 repeats=8；struct_records ticks=[2963, 2963, 2964] median=2963 repeats=8；crc16 ticks=[5325, 5325, 5325] median=5325 repeats=8；call_chain ticks=[2796, 2796, 2796] median=2796 repeats=8；IRC=39943200Hz。
ai-fixed-protocol baseline ON：case-0 ticks=[37, 37, 36] median=37 repeats=1；case-32 ticks=[90, 91, 90] median=90 repeats=1；case-63 ticks=[7291, 7290, 7291] median=7291 repeats=1；IRC=39943200Hz。
ai-fixed-protocol candidate ON：case-0 ticks=[35, 35, 35] median=35 repeats=1；case-32 ticks=[89, 89, 88] median=89 repeats=1；case-63 ticks=[7289, 7289, 7289] median=7289 repeats=1；IRC=39943200Hz。

以下为历史记录（原字节保留，历史授权不自动适用于本轮）：

MX251 V0.0125：纯远地址参数搬运，开发版、非 LST、未安装
0018-pure-far-address-send.patch 基于 V0.0124，仅改 src/mcs251/gen_native.c.inc，沿用 GPL。
新增 nativePureFarAddress 只接纳原始操作数为三字节 FPOINTER、非函数地址、同宽原生槽、
非 volatile/atomic、非 aggregate、非 cast remat、非 PDATA 页地址的 AOP_IMMD。
PDATA 的 P2/MXAX 读取、CODE/generic/函数指针及其它内存源继续使用原路径。
沿用 ABI4 的先寄存器并行搬运、后纯地址立即数填槽；不重构 SEND、不扩大 wide 准入，
不改变 ABI、运行库、启动、寄存器调用者保存或八字节真实栈参数布局。
固定协议 main.c:111 的参数准备位于 TR0 计时窗内；不是 main.c:114 的 checksum 发送优化。
ON/OFF 候选与严格资格矩阵各18完整镜像及6预期拒绝通过；每模式仅 ai-fixed-protocol 的HEX变化，
另外17份与 V0.0124 完整HEX相同。协议ON 21540->21512B，OFF 21552->21524B，均减28B。
严格资格结果由 qualified-ON/OFF/results.json 记录；硬件结论与恢复状态只以本轮最终报告、
独立原始采集审查及逐镜像映射为准。候选体积变化不是实测周期收益，不宣称稳定提速。
本轮AI计划为 V0.0124/V0.0125 的ON/OFF四行固定协议配对，写入后必须恢复约定数学HEX。
身份检查保持既有桥路径、COM6硬件标识、型号/BSL/Magic/IRC及其余53状态字节的严格匹配。
经用户明确授权，仅本轮当前样板允许零基status[14]变化并完整记录，不据该字节差异拒绝；
identityMaskUsed=true，字段语义未知，不永久扩大白名单。保留单租约、AI不写配置、无Flash回读
及指定数学HEX恢复边界；恢复仍保留历史NaN id132。
17x2同HEX映射只继承原来的资格范围；原本仅离线验证的项目不会因此获得实板资格。
STC镜像均未变化，本轮不新增STC写入或运行验收。不同HEX不能以近似体积或源码借用资格。
两份acceptance原样保留严格构建时的“MX251 V0.0125 candidate”标签与原matrixSha256。
正式开发显示名由VERSION.json记录，不回改原qualified results、profile.lock、HEX或编译器。
本轮目标为固定工程实际成本，Keil逐工程size/time门槛仍待达到；不以指令条数替代周期测量。
MX251继续提供独立公开命令行和CMake+Ninja入口，不依赖StudioX服务、项目元数据或安装目录。
IDE只是适配层，既有SDK/IDE安装版未更新；本记录不授予LST、跨设备或安装集成资格。
证据入口：.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0125/report.html。

以下为历史记录（原内容完整保留）：

MX251 V0.0124：共享远整数搬运，开发版、非 LST、未安装
0017-far-integer-word-transfers.patch 基于 V0.0123，仅改 src/mcs251/gen.c，沿用 GPL。
在原 legacy genFarPointerGet/Set 中，仅接纳三字节 FPOINTER、同宽 16/32 位整数、
非 volatile/atomic、完整 AOP_REG，且整组仅使用 WR8/WR12/WR14。先验证全部条件，
再按大端物理次序发射 word MOV；保持原末尾 A/DPX 与最后 INC 标志，失效旧 DPX cache。
同时检查原始 iCode 和物化后的指针，保留字节回退；不改 ABI、运行库、启动或 ordinary 准入。
ON/OFF 各 18 完整镜像和 6 预期拒绝严格构建通过；每模式 12 镜像缩小、6 镜像逐字节不变。
固定协议 main 2186->1906 编码B，完整 ON 21820->21540B、OFF 21832->21552B。
计时窗内 main 的 107 条指令不变；静态 MOV 减少不等于周期收益，不能据此宣称整体加速。
本机制 stack/ABI/spill 指令减少为0，整个工程的语义spill/ABI槽总量仍未独立求出。
44函数边界仅成对编译/静态审查，其中7函数实际触发；独立片段模型不等于全ISA或全C证明。
两份 acceptance 必须保留严格构建时的原始字节，包括“MX251 V0.0124 candidate”和
“hardware qualification pending”等历史描述，以保持 matrixSha256；它们不是另一版二进制。
正式开发展示名由 VERSION.json 标明。最新逐镜像硬件资格及恢复状态以本轮最终报告和
qualified-hardware-mapping.json 为准，不改原 qualified results、profile.lock、HEX 或编译器。
AI 实测为18计划镜像及约定数学恢复，数学仍保留既有 NaN id132 例外（267/268）。
STC变化镜像按完整HEX去重为10份，另需一次指定整数/Timer0恢复；只有整数small/large的
ON/OFF各自相同。新镜像不能借用历史不同HEX的资格，不能用近似大小或源码替代哈希。
AI流程不写配置；STC官方流程含保留原配置尾部的04命令。两板均无物理Flash回读。
STC运行HID尚未绑定BSL容器身份；原端口回归及完整状态重核证据必须保留，不扩大设备白名单。
同源Keil OPT7协议19160B，对比本版21540/21552B仍未达到逐工程size/time<=Keil100%的最低目标。
独立CMake+Ninja入口、工具内容锁和既有安装版保持独立；不升级SDK/IDE或授予LST资格。
完整证据：.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0124/report.html。

以下为历史记录（原内容完整保留）：

MX251 V0.0117 更新
AI的原生整数、显式原生数学及MDU32运行库接入既有双器件CMake/Ninja入口；
不再依赖单独artifact构建器。AI的XDATA窗口恢复到R89/V0.0100的0x7000，
使旧完整MCU工程的18KB结果缓存可构建；STC仍0x2000，不更改启动/向量/ABI。
profile锁定相同的当前编译器与库，不是新MDU算法或新编译器优化Pass。
统一矩阵18份完整固件、6配置拒绝、13 profile/旧缓存边界通过；原11份HEX不变。
AI新实测七固定工程通过：MCU1152、协议512、调用1280、数学软件/MDU各18，
MDU small/large整数各6400及Timer0。具体源与镜像见验收矩阵，不推广到所有浮点边界。
当前官网STC核心5文件哈希不变；8项通用负载MX耗时GM为STC的0.685179倍，
与R89相同，因此不能把这份新对照当本次新提速。完整固件5709比6459B小11.612%。
STC数学仍16/18；调用应用仍编译失败；协议同字节镜像两次无回传，历史成功未能
复现，原因未定，不计新速度成绩，不据此断言STC编译器错码。板恢复数学自测。
下一优先：同一完整工程里普通ABI调用端到ABI4的参数装载/栈交换/结果搬运。
identity内核已只有ERET，外层call_integer仍从栈装载两参数、push/pop互换DR4/DR12，
返回再逐字节转A/B/DPTR；不能靠删空函数或删除零边界追成绩。先核对寄存器别名、
栈偏移和跨TU约定，以统一规则减少冗余；比较语料/输入/计时区间保持不变。
IDE新profile集成、large原生数学、广泛ISR/full Flash和强杀恢复仍缺；非LST，未安装。
新增窗口/库准入风险以固定工程与混用拒绝约束；不宣称全器件/全C支持。

历史记录：
MX251 V0.0116 固定工程验收入口

同一 RunAcceptance.py、Project.cmake、BuildLatest.ps1、Build.ps1 验证两颗器件。
九个 STC32G12K128 配置和两个 AI8051U-34K64-32 配置均来自原有完整工程，
要求 HEX 与 V0.0115 逐字节相同。源码、包含目录、宏继续由 CMake 管理，
Ninja 执行构建，MX251 编译链接。acceptance.json 只是资格矩阵，不是工程入口。

调用（所有工具显式指定，保持受管理且不安装全局环境）
python RunAcceptance.py --profiles <profile-roots.json> --toolchain <父工具目录>
  --cmake <cmake.exe> --ninja <ninja.exe> --pwsh <pwsh.exe> --output <新目录>
profile-roots.json 将两个精确 target ID 映射为 profile 路径；相对路径以映射文件
目录为基准。缺项、重名、额外目标及哈希不符均拒绝，不自动猜测芯片。
可复现映射位于 mx251-v0.0116/profile-roots.json；父工具仍 V0.0110/clean-tools。
新 profile lock schema 2 固定 compiler overlay、全部工具/库、启动和目标规则哈希。
编译器和运行库仍为 V0.0115，未重新构建或替换编译器；完整源码流程见 BuildSource.py。

build/qualified/TargetContracts.json 是本入口器件规则的唯一来源；包含启动哈希、
中断初始化说明、向量政策、运行库准入及已验证地址窗口。启动实际机器码未变。
STC 保持 IE/IE2 整字节清零与62向量保护；AI保持原先 EA 位清零与编译器默认向量策略，
不能声称 AI 也有62向量保护。地址窗口不是整颗芯片的物理容量声明。
AI 本入口只准入 standard；这表示此套资格证据范围，不代表芯片没有 MDU 或数学能力。
不得把旧 profile 改 target 名后直接复用。旧 schema 1 实验 profile 不接收新入口，
应使用本次单独生成、已验证的 schema 2 配置；封存历史和安装版均不修改。

固定工程包括：软件数学/MDU32数学/关闭宽后端数学，整数与Timer0 small/large，
原多文件调用 small/large，STC与AI真实原生回调 small/large。receipt schema 4
校验完整 HEX、代码字节、工具、profile、目标规则、启动、向量和内存窗口身份。
六个不支持配置明确拒绝，失败不得发布可下载固件。原始日志保留。

配置混用及失败后的旧固件处理
python CheckTargetProfiles.py --profiles <同一映射> --acceptance <通过的当前验收目录>
  --toolchain <父工具目录> --cmake <cmake.exe> --ninja <ninja.exe> --pwsh <pwsh.exe>
  --output <新目录>
检查换错启动后重算自述哈希、漏锁/重锁启动或浮点库、错误目标规则引用、
残留AI/STC中断元数据、缺失编译器overlay等。每次预置明确标注的模拟旧缓存：
取自已验收的真实 HEX/receipt，加 BIN 哨兵；失败后必须全部失效且不能启动编译。
此脚本只在新隔离目录中测试，不下载硬件，也不接触用户工程。

历史 ABI 边界入口保持
python CheckNativeIndirect.py --compiler <sdcc.exe> --toolchain <父工具目录> --output <新目录>
覆盖范围和未支持类型见 stc32g-callbacks/README.txt。尚未将历史全部 ABI/ISR/浮点
边界迁入同一调度器；不能用本轮配置测试数量代替编译器语言/精度资格。

硬件与性能边界
两器件四种回调配置使用 V0.0115 的已封存实板证据：每种2560结果、6边界及Timer0。
本轮11份HEX全部相同，继承对应历史证据，不把离线重建算作新实板运行。
数学和整数证据及wide-off仅离线限制见 acceptance.json。ACK与运行回传分列，未回读Flash。
本轮固件体积变化0%；运行耗时未重测，没有新的 C251/STC-IDE/初版 SDCC 成绩。
没有硬件访问，两板继续保持原自测固件；profile 中的地址和启动不同，不互换烧录。

剩余交付工作
尚未将研发 schema 2 profile/receipt 4 接入已安装 IDE；不是发行包或 LST。
IDE集成、配置阶段失败和强杀恢复、精确增量依赖、跨宿主重建、完整浮点边界、
large原生数学、广泛中断和全Flash仍未完成。未修改产品版本、系统PATH或默认工具链。


V0.0118：CheckNativeStackSend.py --parent-compiler <parent sdcc.exe> --compiler <current sdcc.exe>
--toolchain <locked tools root> --output <new evidence directory>。
36项检查覆盖宽度、volatile、取址、第三栈参、间接/普通调用、混合寄存器参数等；
atomic仍明确error277拒绝。该脚本只做离线对象/诊断检查，不是硬件或速度证明。
硬件语义边界及固定工程证据见V0.0118封存记录。RunAcceptance仍严格校验18镜像及6拒绝。
每次优化报告列出真实受影响函数的MOV、PUSH/POP、显式SPX字节/字访问、ABI转换、
spill及size/cycles/correctness。静态数量不冒充动态周期；无法证明的项目明确标未测。
测试入口、应用模块和完整固件分别标记；负收益与失败候选不可隐藏。


MX251 V0.0119：函数及调用点影响审计（尚不删除保存）
0012-call-effects-audit.patch 基于 V0.0118 有效源码，遵守原 GPL 许可证。
instruction_effects.h 复用 register_facts.h 的物理字节重叠；已知指令记录 may-read /
may-write，未知指令、SFR/未限定指针、调用、外部跳转统一为 top。flags 整体保守。
只发布最终接受的宽函数；失败/旧后端不发布可信摘要。非净破坏集合，不改变 ABI。
调用点分别记录 live、原 PUSH 组宽、参数准备、结果搬运的物理写入，防止只分析 callee。
CMake/Ninja Build.ps1 生成 call-effects.json；收据绑定编译器、profile、输入快照、
ASM/REL 和固件哈希；AnalyzeCallEffects.py 校验来源后只报告需继续证明的保存组。
未接入跨模块再编译、没有自动删 PUSH/POP、没有公共代码抽取或新运行库。
18 份 HEX/汇编及宽分配记录与 V0.0118 完全一致；6 项拒绝、331 个最终函数摘要、
共享头文件语义检查和 17 项 sidecar 完整性检查通过。无新周期成绩，无硬件写入。


MX251 V0.0120：受限叶函数反馈，默认关闭，非 LST
0013-leaf-call-feedback.patch 基于 V0.0119 有效源码，沿用 GPL；只改变 main.c、
wide.c、wide_emit.c.inc，新增 wide_leaf_effects.c.inc。没有引入 C251 源码。
按 source-verification.json 命令应用补丁：git -c core.autocrlf=false -c core.eol=lf apply。
源码保留各文件实际换行；完整源码重建输入锁和最终 compiler SHA 均已核验。

使用 CMake -DMX251_LEAF_CALL_FEEDBACK=ON 显式开启；默认 OFF。CMake 管理源码、
头文件和宏，Ninja 调用 MX251。第一遍生成已接受宽函数的保守 may-write；只选当前
导出且唯一、无调用、无内存写入的叶函数。第二遍重新编译 C 单元。只删除整组不冲突
保存，同时考虑 callee、调用者实参准备、结果搬运/清栈；SPX 位移重新发射。再次核对
实际影响及指令/字节代价，冲突或代价不合格保留原候选，不直接删除汇编文本。
来源绑定编译器、profile、输入快照、第一遍 ASM/REL/LST 和最终 callee 完整正文、
ABI、may-write；失效拒绝发布固件。不能用陈旧 sidecar 跨构建偷省第一遍编译。
这是 may-write 上界，不要求证明净 clobber；不把 callee 内保存恢复视为不破坏。
未知/legacy/运行库/汇编/间接调用和递归边不消费摘要。递归函数内部对普通叶函数的
直接调用可获益。原生回调路径仍按既有整函数回退，无新增间接调用优化。
底层 --mcs251-leaf-effects 文件是显式可信实验契约；直接传伪造掩码不受保证。
构建驱动的哈希用于失效与误混检测，不是防恶意修改构建工具/汇编的安全边界。

RunAcceptance.py 默认使用 acceptance.json；--leaf-call-feedback 使用独立锁定的
acceptance-leaf-feedback.json。两种模式各 18 完整镜像 + 6 拒绝，精确校验 HEX、
收据、profile、编译器与反馈来源。ON 的 8 份变化镜像有本版双目标实板证据，其他
10 份逐字节继承已封存基线；OFF 全部等于 V0.0119。22 边界组、9 失效检查通过。
STC32G 两模型直接/回调各 2560 结果 + 中断；AI 两模型回调和固定调用/协议通过。
两板已恢复原自测并释放租约。没有 Flash 回读，下载 ACK 与运行采集分别记录。
静态实际收益：calls 双模型各 -100B/-25 对 PUSH/POP；回调与 AIcalls 各 -48B/
-12 对；协议 -16B/-4 对。MOV、显式 SPX 字节/字访问、ABI 转换和 spill 减少均为0。
详细计时和真实函数见 mx251-v0.0120/report.html，单项提升不能替代完整应用结论。


MX251 V0.0121：调用影响驱动的寄存器候选，默认关闭，非 LST
0014-call-aware-allocation.patch 基于 V0.0120，只修改 src/mcs251/wide.c，沿用 GPL。
应用补丁仍按 source-verification.json 使用 Git autocrlf=false/eol=lf，保留实际换行。
当前已绑定的导出唯一叶函数 may-write 信息用于额外分配候选，跨调用活值尽量避开
被覆盖的物理字节；保留原顺序/返回偏好/固定参数/冲突/溢出规则。摘要适用范围、ABI、
运行库、启动及未知调用回退均不扩张。实际发射再次验证调用准备、结果搬运及保存，
新候选仅在已知编码字节、加权指令和栈帧不增时参与。启发式不保证真实周期不增。
仍通过独立 CMake + Ninja 的 MX251_LEAF_CALL_FEEDBACK=ON 启用，无 IDE 服务依赖。
两模型调用工程各省28B/MOV2/PUSH6/POP6；回调每镜像省26B/MOV3/PUSH5/POP5。
显式栈 byte/word/dword、spill 和语义 ABI 边界均不减。受影响 c_native 计时下降
6.42%–6.84%，c_float 约3.23%–3.26%；十函数普通路径几何平均约1.10%–1.14%，
并非完整固件加速比例。协议只计4B收益：同条件重测短输入中位数多1tick，长输入少2，
保留差异，不能声称该工程确定提速。Timer0 tick 不是 CPU 指令周期。
OFF/ON 各18镜像+6预期拒绝、22边界组、保存集合和干涉区间审计通过；两板受影响
固定工程运行通过，原自测已恢复并释放租约。未做Flash回读，ACK与运行证据分开。
完整源码、输入锁、补丁、原始采集和逐函数数量见 mx251-v0.0121/report.html。


MX251 V0.0122：入口不可变指针副本合并，非 LST、未安装
0015-entry-pointer-copy.patch 基于 V0.0121，只修改 src/mcs251/wide.c，沿用 GPL。
仅在入口单后继前缀、同地址空间、原指针唯一 RECEIVE 定义及副本唯一赋值时，
合并被证明相同的 MIR 值；重算活跃性/干涉/权重/调用保存。不缓存或删改内存事务。
保留完整原 MIR 的分配结果；两方案均完整发射后，编码字节、加权指令、帧均不增
且至少一项降低才选择新方案；未知或不适用保持原代码，错误仍报错。
此优化随宽后端工作，不依赖 leaf feedback。反馈默认 OFF 及 opt-in ON 各18固定
镜像+6拒绝通过。每种模式只有协议 parser_feed 变化，其余17镜像与V0.0121相同。
parser spill6→2、frame24→8；digest 每轮SPX访问23→7，byte1→1/word22→6，
ABI转换0→0。静态MOV104→91；ON PUSH/POP各2→3，OFF各7→6，如实保留新增保存。
完整协议OFF 21990→21928B、case63 7666→7360tick；ON 21970→21916B、7566→7294。
case0均40→40、case32均94→93；Timer0=SYSCLK/12、39943200Hz，同会话配对。
两模式各512结果通过；AI数学已恢复267/268仅历史NaN132，租约释放；无Flash回读。
STC32G镜像未变，沿用同HEX既有证据，本轮没有重复写板。
相对有效Keil19160B，ON仍为114.38%；case0/32仍慢，不满足逐工程最低线。
完整源码输入锁、独立ASM解释/对象事务检查、patch复现、原始回传见
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-bottleneck-20261027/report.html。


MX251 V0.0123：跨控制流的固定局部对象寻址，开发版、非 LST、未安装
0016-fixed-stack-object-addresses.patch 基于 V0.0122，仅修改 wide.c，沿用 GPL。
仅从完整本地对象、单一定义和真实 CFG 支配关系恢复固定地址；包括跳表后继。
改写原位访存寻址，不缓存内容、不移动读写，不扩 ABI、调用摘要、运行库或入口复制 pass。
完整 V0.0122 选择器作为基线；最终编码字节、加权指令、栈帧均不增且至少一降才采用。
app_run 780->684B、264->234条，MOV少16、ADD少7、ANL少7；PUSH/POP和ABI转换不变。
spill值4->2/帧95->87，spill静态访问20->12；对象直接SPX5->17，总SPX34->38，分别计数。
ON完整21916->21820B，case0/32/63 40/93/7294->37/91/7291tick。
默认OFF完整21928->21832B，40/93/7360->38/91/7357tick；同会话39943200Hz，Timer0=SYSCLK/12。
每模式其他17个固定镜像逐字节不变；ON/OFF各18镜像+6预期拒绝严格重建通过。
独立完整应用64输入/512字段、36地址栈边界、4负控；对象事务逐项相同。
四份AI运行均512/512；数学恢复267/268，仅既有正NaN id132；租约释放，无Flash回读。
12函数边界仅编译/静态审查，不能说已实板覆盖；STC32G本轮镜像未变。
逐工程Keil门槛仍未达，不以长输入或平均值掩盖空/短路径差距。
完整补丁/输入锁/原始回传/指令与SPX分类/未完成项见
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0123/report.html。
