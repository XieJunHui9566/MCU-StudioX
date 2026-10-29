# 图形配置的工程范围与时钟托管

2026-10-22 已实现的可选扩展。适用于 API 3 插件，分别声明 `projectMatching`、`projectClockV1` 宿主能力；旧宿主拒绝未知能力。本文描述首版单级时钟树，完整 `mcuConfigurationV1` 提供者和多文件生成事务仍见[设计契约](GRAPHICAL_CONFIGURATION_PLUGIN.md)。产品版本、Pack/工程格式 1 不变。

## 工程范围

配置插件使用 `scope: "project"`。清单 `supportedProjects` 的每一行包含准确 `packId`、`packVersion`、`deviceId`、`templateId`、`packageId`、`profileId`、`profileVersion`；最多 64 行，各行独立比较，不展开型号范围。对应工程必须显式关联 `device.mcuconfig`，文件的 `profile.pluginId`、target 和 origin 必须与清单和工程来源一致。

应用服务在启动插件进程前判断适用性。无工程、其它型号、其它封装、其它模板、相同 MCU 的普通工程均无功能入口、命令、面板或配置插件进程；切换工程移除旧贡献，迟到消息沿用既有会话代次保护。已安装插件仍显示在全局管理目录，便于启停和卸载。其它通用插件继续使用原有工程/应用范围。

`mcuConfigurationPreview` 即使未声明新的准确组合，也至少要求显式配置文件与插件身份关联；不能因插件已安装就在所有工程显示。

## 时钟数据

清单声明 `managesProjectClock: true` 时，同时需要准确工程范围和 `mcuConfigurationPreview` 能力。该声明只管理匹配工程。基础资料中的绑定使用以下形状，具体节点和允许值由准确 profile 决定：

```json
{
  "contractVersion": 1,
  "systemNodeId": "SYS",
  "sourceKinds": { "IRC": "internalRc", "XTAL": "externalCrystal" }
}
```

绑定存于 `definitions.profileData.projectClock`。`configuration.clocks` 中源节点 `parameters.frequencyHz` 是分频前整数 Hz，系统节点的 `inputId` 选择源节点，`parameters.divider` 是系统分频；系统 Hz 由源频率除以分频派生，不再保存第二个独立频率。

`configuration.profileData.projectClock` 保存 `{ managed, valid, inputSha256 }`。模板默认未托管、未校验且摘要为空。显式保存时插件设置托管状态和校验结果，将完整文档去掉该对象的 `valid/inputSha256`、令 `generation=null` 后做 JCS SHA-256；配置与凭据在同一次正常文件保存中发布。该摘要检测过期修改，不是作者签名或权限沙箱。完整配置含外设规则，校验失败时不得用旧有效时钟继续操作。

创建工程时保留已校验器件包的文件索引 `.studiox/pack-files.sha256.json`。核心重新核对索引内容摘要、模板原始字节摘要、基础资料 JCS 摘要、准确目标/profile 与工程 origin，不接受配置文件自行替换的时钟规则。需要插件校验的工程如存在未应用表单、未保存图形草稿或同文件脏编辑缓冲区，构建和下载均报告未就绪。

## 下载与构建

匹配且已启用的插件立即锁定手动时钟入口；没有有效保存值时显示待配置，不回退旧手动频率。下载页只读显示来源、源频率和分频后系统频率，提供“前往时钟树配置”；端口和传输波特率仍可编辑。

STC ISP 接收分频前源频率。编译和计时计算使用分频后的系统 Hz；受管构建同时定义 `STUDIOX_CLOCK_HZ` 和 `STUDIOX_CLOCK_DIVIDER`。首个 STC15 配套模板带独立的第一方 SDCC 启动钩子，按明确的 1/2/4/8/16/32/64/128 分频写入 `CLK_DIV` 低三位，保留其它位；无托管宏时不写时钟寄存器。源选择和 RC 目标频率只在用户随后明确下载时经 ISP 设置，保存本身不接触硬件。当前不允许受管时钟与 Mon51 调试构建组合。

`.studiox/stc-isp.json` 在托管模式只保留端口、传输波特率等下载参数，不复制源频率；旧手动时钟被清除。后端保存 API 也拒绝覆盖托管来源/频率，命令调用不能绕开只读控件。构建凭据包含配置文件，下载准备快照绑定校验时的配置摘要，执行前再核对；文件修改、分频变化、未保存修改或旧固件均需重新检查/编译。

## 停用、恢复与版本

停用、卸载或插件进程故障不解除已保存的 `managed` 状态，仍用有效保存配置；配置损坏时保持锁定并报告原因。停用匹配提供者后，用户可明确点击“解除时钟托管”。应用先清理旧手动时钟，再通过正常文件服务修改配置；失败时不会恢复历史频率。解除后从“保留当前来源”开始手动选择。重新启用匹配提供者再次要求图形保存校验。

STC 试行版 0.1.3 使用 profile 2.2.0 和配套包 0.1.3，准确支持 IAP15L2K61S2/LQFP44/裸机 C，并加入[按模块生成代码](GRAPHICAL_CONFIGURATION_GENERATION.md)及 main 模板接入点。旧 profile 1.0.0 的频率含义不同，不能静默解释成源频率；2.1.0 已加入厂商频率预置资料。使用新的配套模板创建工程，已有工程不自动迁移。上述能力经过离线构建与界面检查，不代表实板频率或外设时序验收。

## 时钟图与可编辑频率

`mcuClockDiagramV1` 是 API 3 的独立可选能力，须与 `mcuConfigurationPreview` 一起声明。`PluginMcuClock` 增加 `kind`（`source/multiplexer/divider/junction/sink/block`）、`symbol`、`detail`、`enabled`、`activeInputIds`，保持原有 ID、输入引用、坐标与显示值字段。选中输入必须属于已声明输入；坐标限定 X≤2400、Y≤1600，文本及节点数量沿用有界校验。宿主只画内置符号与直角连线，不接受插件自带 XAML、路径程序或芯片专属代码。绿色表示配置选中路径，灰色表示未选用/未启用；图中数值并非硬件测量。

表单字段增加 `kind: "editableSelect"`：`options` 提供推荐下拉项，`value` 可为其它有界字符串，由插件校验，不要求是预置项。STC 的 `clockMHz` 使用 MHz 文本，最多六位小数，保存时精确换算为整数 Hz，不改变配置文件中 `frequencyHz` 的单位。内部 IRC 校验 5–28 MHz，外部晶振需填写实际装配器件参数。

本机 STC-ISP 6.97C 的准确 IAP15L2K61S2、控件 1045 中核对到 5.5296、6、11.0592、12、18.432、20、22.1184、24、27 MHz 共九项。控件本身允许输入；这些是厂商软件预置，不是硬件只能取这些频率的枚举。该列表以 Hz 存入 `definitions.profileData.ircFrequencyPresetsHz`，应用程序 SHA-256 与核验位置记录在 evidence 和 provenance 中，不分发厂商程序。

## 交互视口与图内参数

封装画布支持左键拖动、以鼠标位置为中心的滚轮缩放、滑条缩放与居中适配；拖动超过系统阈值才占用鼠标，普通单击仍打开引脚功能菜单。平移和缩放在配置刷新及切页后保留。

可选 `mcuClockFieldsV1` 在 `PluginMcuField` 加入 `clockNodeId`。只有时钟页字段可绑定，必须指向已声明节点，且每个节点最多一个字段。桌面通过内置控件绘制，不执行插件 UI。STC 将频率输入绑定选中源，时钟选择绑定 MUX，系统分频绑定分频器；下方保留应用参数按钮。布局参考 [STM32CubeMX UM1718 的 Clock Configuration 视图](https://www.st.com/resource/en/user_manual/dm00104712-stm32cubemx-for-stm32-configuration-and-initialization-c-code-generation-stmicroelectronics.pdf)，器件拓扑仍来自 STC 已核对资料，不添加 PLL 或 STM32 总线。

时钟页和外设页的选择变化自动应用到同一份插件内存草稿，文本输入停止 400 ms 后合并检查，刷新频率、选中连线和时钟源上的输入位置。切换页签前先提交当前输入，因此 Timer0/1 的 1T、12T 和启用状态会带入时钟树。刷新保留编辑焦点与光标；无效输入保留在控件中并提示图中仍为上次配置。保存配置、生成代码和硬件下载继续分别执行，自动刷新不写工程文件。

`mcuPinActionsV1` 是 API 3 的独立可选能力。`PluginMcuPin` 增加默认关闭的 `locked` 和 `canEditMetadata`，旧提供者不启用命名及锁定菜单。封装引脚左键打开功能菜单，右键打开命名及锁定菜单，不再展示重复的“当前引脚”侧栏。STC 通过 `pinLabel` 和 `pinLock` 动作更新草稿，锁定保存为已分配引脚上的可选 `locked: true`，不改动基础资料、准确 profile 或器件包身份。锁定保护功能分配，不代表硬件寄存器锁；外设换脚同样受插件约束。未改变的 UART 分配保留名称与锁定，保存后重新读取仍有效。
