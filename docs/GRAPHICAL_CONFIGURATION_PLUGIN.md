# MCU 图形化配置插件契约 v1

状态：设计草案，2026-10-21；未实现的扩展契约。必须与[文件与生命周期标准](GRAPHICAL_CONFIGURATION.md)一起阅读。现有 API 1/2/3 不因本文出现而获得新的能力；本文只保留拟用名称，不修改现有可执行代码、SDK 或清单。

2026-10-22 的[试行插件](../plugins/StudioX.StcConfigurator/README.md)以 `mcuConfigurationPreview` 声明有限视图和单文件保存，并以独立的 [mcuCodeGenerationV1](GRAPHICAL_CONFIGURATION_GENERATION.md) 实现用户要求的直接生成、模块拆分、main 接入和有恢复记录的事务。不实现本文的完整提供者协议；不能仅因使用 `.mcuconfig` 就判定完整 v1 合规。

已实现的 `projectMatching`、`projectClockV1` 及其清单和文件字段见[工程范围与时钟托管扩展](GRAPHICAL_CONFIGURATION_CLOCK.md)，不要与下文拟用的 `configurationProviders` 支持组合混用。

## 1. 扩展识别与兼容

拟增加能力标识 `mcuConfigurationV1`，贡献集合 `configurationProviders`，每个提供者声明 `contractVersion: 1`。插件仍需满足已有安装、显式启用、内容指纹、独立宿主、取消和退出约束。

本契约为现有项目级插件协议的可选扩展，使用既有 stdio 版本 2 包络。SDK 接入时须显式增加宿主能力检查、贡献验证及新的 invoke 分派，不假设当前 API 3 已接受它。可以在支持此扩展的 API 3 宿主上实现，不要求为本次文档准备升级产品版本或全局 API 号。旧宿主应按原规则拒绝未知能力，不能把贡献降级成通用按钮后绕开检查。

提供者 ID 在其插件内唯一。每个提供者必须列出准确支持组合，不允许只声明厂商、系列前缀、模糊料号或版本范围：

```json
{
  "id": "demo-configurator",
  "contractVersion": 1,
  "support": [
    {
      "vendorId": "example",
      "deviceId": "DEMO4",
      "packageId": "DEMO-4",
      "profileId": "example.demo.bare",
      "profileVersion": "1.0.0",
      "dataSchema": {
        "id": "example.demo.data",
        "version": "1.0.0",
        "sha256": "示例中需替换为准确 Schema 文件的64位小写SHA256"
      },
      "framework": { "id": "bare-metal", "version": "1.0.0" },
      "sdk": null
    }
  ]
}
```

以上为字段说明片段，不是可安装的插件清单。支持组合按一行一组进行比较，不能将多个独立列表做笛卡尔积后误开放未验证组合。宿主只从文件指定的 `pluginId/providerId` 中选择，任何一项不匹配都报告不可用；不按“同系列”“更高版本”自动替换。提供者最多 16 个，每个提供者最多 1024 条支持组合，总贡献仍须满足现有描述消息上限。

插件可以支持多个已发布的准确 profile 版本。更换插件实现而 profile 不变，仍需在生成预览里显示插件版本/内容变化。已经生成的源码不依赖插件继续运行。

## 2. 调用上下文

拟使用 `invoke(kind: "mcuConfiguration", id: providerId)`。`arguments` 根字段固定为：

| 字段 | 类型与语义 |
| --- | --- |
| `contractVersion` | 整数 `1` |
| `operation` | `evaluate`、`propose`、`planGeneration` 或 `planMigration` |
| `context` | 下表的只读上下文 |
| `document` | 完整且通过公共校验的工程 `.mcuconfig` 内存快照 |
| `payload` | 对应操作的参数对象，不能包含其它未声明字段 |

`context` 包含 `workspaceEpoch`、`documentRevision`、`documentPath`、`inputSha256` 和 `files`：

- `workspaceEpoch` 为宿主生成的不透明工程代次字符串；不含工程绝对目录。
- `documentRevision` 为当前编辑文档单调递增的非负安全整数；与磁盘时间戳无关。
- `documentPath` 为已关联的项目相对路径 `device.mcuconfig`。
- `inputSha256` 使用主标准的生成输入投影。
- `files[]` 为本次允许读取的相关文件快照，每项包含 `path`、`exists`、`bufferRevision`、`diskSha256`、`content`、`contentSha256`。不存在的文件除 `path/exists` 外均为 `null`；未打开的文件 `bufferRevision` 为 `null`；脏缓冲区内容摘要与磁盘摘要可以不同。

同路径只提供一份最新快照，插件不能将旧磁盘内容覆盖用户未保存的编辑内容。插件可请求已有只读宿主工具补充资料，继续检查工程范围和实际权限；计划依赖的所有内容最终必须加入计划前提。不能读到一份内容却不声明其摘要和依赖。

所有响应必须原样带回 `contractVersion`、`operation`、`workspaceEpoch`、`documentRevision`、`inputSha256`，以及 `diagnostics` 和对应操作的 `result`。宿主逐项核对，过期响应只保留诊断，不应用界面或文件变更。生成计划另有独立有效期，不能因为 invoke 已返回就免除应用前复核。

## 3. 操作

| operation | payload | result |
| --- | --- | --- |
| `evaluate` | 空对象 | `{ forms, actions, derived }`；纯求值，无文件修改 |
| `propose` | `{ actionId, arguments }`，引用最近一次有效 evaluate 中的动作 | `{ configuration }`；返回完整候选用户配置，所有其它文件字段不变 |
| `planGeneration` | `{ outputPolicy: "separate-files-v1" }` | 下节的完整生成计划；不能实际写盘或启动工具 |
| `planMigration` | `{ target, profile }`，目标必须显式选定且可由目标提供者支持 | `{ document, mappings, unresolved }`；候选完整文档及逐项映射/未映射说明；不产生源码 |

`planMigration` 由目标提供者执行：宿主先匹配目标支持组合，允许其读取通过公共校验的源文件，并检查其另外声明的源 profile 精确列表 `migrationSources`。普通求值/生成仍只接受自身 support 组合。该调用不允许普通动作绕过只读基础资料；来源包和新的绑定资料由核心从明确选择的包校验并提供，不能仅相信插件自己填写的 `origin`。

插件不能在上述操作中通过通用写工具绕开修改计划，也不能启动编译、下载或调试。`hostTools` 声明不等于用户授权；信任插件具有当前用户的 OS 权限，这里的规则是应用桥接契约和审核要求，不宣称阻止恶意插件自行访问文件。

### 3.1 通用图形与表单数据

封装图由 `definitions.package/pins` 呈现，时钟关系图由 `definitions.clockTree` 呈现；插件不提交 XAML、HTML、可执行 SVG、WPF 控件、窗口句柄或自定义程序集。两端图形界面复用相同的数据和动作含义。

`evaluate.result` 固定为下列结构：

- `forms[]`：`{ id, title, fields }`。最多 64 个表单，总共至多 256 个字段。
- `fields[]`：`{ id, label, kind, pointer, value, unit, readOnly, options, minimum, maximum }`。所有字段均存在，不适用的 `unit/minimum/maximum` 为 `null`，没有选项时 `options` 为空数组。
- `kind` 为 `text`、`boolean`、`integer`、`number` 或 `select`；`options[]` 为 `{ label, value }`，值为字符串、布尔值或安全数值，最多 256 项。`value` 为同类标量或 `null`，边界使用有限数值。`readOnly: true` 的字段只展示。
- `pointer` 为指向 `/configuration/...` 下已存在值的 RFC 6901 JSON Pointer。宿主必须解析并检查父节点、类型和转义，不能用字符串前缀检查代替；不得指向基础资料、来源或生成凭据。修改只形成候选配置，再经求值和用户操作应用。
- `actions[]`：`{ id, title, argumentSchema }`，最多 64 项，名称在本次求值内唯一。参数 Schema 使用现有插件工具的有限 Schema 子集，最多 64 KiB，总响应仍受协议上限约束；拒绝未声明动作、Schema 不符及过期动作。动作可用于分配/释放引脚、选择时钟、增加/移除外设以及规则求解建议。
- `derived` 固定为 `{ clocks, resources }`。`clocks[]` 每项为 `{ nodeId, state, frequencyHz }`；`state` 为 `known`、`unknown` 或 `disabled`，仅 `known` 的频率为 `{ numerator, denominator }`，两者为规范非负十进制整数字符串，分母大于零，分别最多 128 位；其它状态频率为 `null`。
- `resources[]` 每项为 `{ id, state, message }`，表达使用、冲突或未知状态；`state` 为 `available`、`used`、`conflict` 或 `unknown`，最多 1024 项。它是可观察结果，不能自动提升为设备实测状态。

表单 `id/field.id/action.id` 使用主 Schema 的标识符规则；标题/标签最多 256 字符，`unit` 最多 32 字符，消息最多 4096 字符。每个表单内字段 ID、全部动作 ID、派生时钟 ID 和资源 ID 各自唯一。派生时钟至多 512 项且必须引用已有节点。

参数联动导致额外修改时，插件必须通过 `propose` 返回完整候选配置，让用户看清变化；不能只显示新值却悄悄覆盖其它参数。冲突状态不自动复用其它引脚或改变外部晶振频率。

### 3.2 诊断对象

`diagnostics[]` 每项固定为 `{ code, severity, message, pointer, evidenceIds, relatedPointers }`。`severity` 为 `info`、`warning` 或 `error`；`pointer` 为当前文档中的合法 JSON Pointer 或 `null`，其余指针同样需合法；证据 ID 必须存在。最多 512 条诊断，每条消息最多 4096 字符，代码使用稳定 ID，不以翻译文本作为代码。

阻断错误时 `planGeneration.result` 必须为 `null`，宿主也独立拒绝生成。未完配置允许求值和保存。未知规则、不可解频率或插件异常不能返回空诊断并宣称成功；异常保留原始进程/协议信息。

## 4. 生成计划

`planGeneration.result` 为：

```text
planId, inputSha256, reads[], outputs[], buildEdits[]
```

- `planId` 为本会话唯一的不透明 ID；计划只对当前工程代次和前提快照有效，不可跨工程或重启复用。
- `inputSha256` 必须与调用上下文一致；插件不得用不同配置计算输出。
- `reads[]` 包含本次快照依赖的全部文件，项结构为 `{ path, exists, bufferRevision, diskSha256, contentSha256 }`。配置文件本身、上一份凭据拥有的所有输出、准备变更的构建文件，以及影响输出的其它读取均必须包含。宿主还核对已记录的 SDK/器件资源与插件内容身份。
- `outputs[]` 每项为 `{ path, operation, expectedSha256, content, contentSha256 }`。`operation` 为 `create`、`replace` 或 `delete`。`create` 的前提为目标不存在且 `expectedSha256: null`；其它两者必须有与上一份凭据及当前文件一致的准确原始摘要。
- 创建/替换的 `content` 为完整 UTF-8 文本（无 BOM），`contentSha256` 为其字节摘要；删除操作的这两项均为 `null`。输出限定主标准的两个生成目录和 C/C++ 头/源文件，不允许额外二进制、脚本或 SDK 修改。最多 128 项，累计创建/替换内容 128 KiB。
- `buildEdits[]` 每项为 `{ path, expectedContentSha256, content, contentSha256 }`，表示针对已有用户构建文件的完整候选文本，最多 16 项。v1 仅允许根 `CMakeLists.txt`，不得创建构建文件或更改其它路径；Application 必须独立证明修改只登记/移除本计划拥有的源码，不允许任意命令、参数、依赖和已有用户规则改写。

未变化的既有受管输出可以不出现在 `outputs` 操作列表中，但仍必须出现在 `reads` 中且摘要与上一份凭据相符。新的完整输出集合严格等于“上一份受管集合减去显式 delete，再加入 create/replace”；插件不能靠遗漏条目放弃所有权、遗留仍参与编译的旧文件或接管其它文件。构建登记也必须与该完整集合一致。

输出内容默认 LF；已有生成文件采用其它换行时，计划须明确展示字节差异；新源码在相同目标、profile、配置、SDK及插件内容下应产生相同字节，不嵌入本机绝对路径、随机值或当前时间。

同一路径只能出现一次，`outputs/buildEdits/配置文件` 不得冲突。所有路径还需通过实际路径边界、祖先链接、Windows 保留名、大小写冲突与只读检查。删除遗漏旧输出、新增未归属文件覆盖、伪造 expectedSha256、遗漏读取或改动无关 CMake 内容时拒绝整个计划。

配置文件及 `generation` 凭据由宿主构造，不接受插件把它们混入 `outputs`。宿主从已验证输出计算新的凭据，绑定实际启用插件内容；凭据的 `outputs` 是生成后仍存在的全部受管输出，不含被删除文件和用户 CMake 文件。

没有源码变化但更新生成器身份时，也需要预览及新的配置凭据；不能后台重签。没有初始化输出的空配置是否可生成由准确 profile 决定；若允许成功，仍需输出至少一份有稳定入口的初始化源文件，以满足非空输出凭据。

## 5. 预览、应用和保存

宿主拥有预览、当前工程状态和写入，不把文件写权限交给渲染器。UI 的用户点击可构成对应动作授权；Agent/插件发起的写入继续经过已有审批。批准后重新复核，不能以“插件已启用”代替该次操作授权。

应用前至少重新检查：

1. 工程代次、当前关联、配置 revision 和语义摘要仍一致。
2. 每项 read 的存在状态、磁盘字节摘要和缓冲区 revision/内容摘要仍一致。
3. 基础资料、来源、profile Schema、实际插件内容和 SDK 身份仍一致。
4. 输出所有权、目标不存在/原内容条件、只读、路径边界和构建登记仍满足。
5. 预览中的全部新增/替换/移除和凭据更新均可以一起应用。

应用是一个可整体撤销的内存操作，新的文件使用暂存文档。任何前提变化、取消、错误或切换工程都应拒绝全部应用；不能先写 GPIO 源码，再发现时钟/CMake 失败。

持久化使用有恢复记录的完整生成保存事务：记录旧/新摘要和必要备份，先准备全部内容，再保存输出及构建文件，最后发布配置凭据和完成标记。必须考虑磁盘满、断电/蓝屏、进程终止、外部修改和只读状态。失败或重启时提供可核对的恢复/回退方案，绝不能让旧凭据替新输出背书；用户后续修改优先保留，不自动覆盖。

多文件文件系统事务不被视为天然原子。实现验收必须覆盖任一步骤失败后的诊断、备份保留、完成标记和恢复行为。常规草稿恢复与生成事务记录分别保存到用户数据/恢复目录，不把缓存、绝对路径或临时锁混入共享工程文件。

## 6. 生命周期、性能和权限

`evaluate/propose` 默认 5 秒超时，`planGeneration/planMigration` 默认 30 秒；取消后拒绝迟到结果。每个文档串行求值，连续编辑合并，只保留一个最新待处理快照；计划不与同文档写入同时应用。超时保留诊断，不自动重复生成或放宽限制。

协议整体遵守主标准的字节/字符上限和现有宿主请求容量，不因新能力扩大全局限制。额外文件快照过大时，在调用前明确报错；不悄悄裁掉依赖内容。v1 不包含分片、共享绝对路径或另起网络服务传输。

停用、卸载、宿主崩溃、关闭配置视图和工程切换必须取消相关在途工作并释放订阅；内存配置/源码由 Application 拥有，插件进程结束不丢失。工程切换仍遵守原有 Agent/MCP、插件、预览、调试和语言服务退出顺序。

图形化配置全过程不拥有设备连接，也不隐式烧录、擦除、解锁、修改选项字节或启动调试。生成、编译和硬件操作是独立用例，分别记录输入、授权和验证结果。

## 7. 实现前的契约检查清单

- 扩展能力、贡献与 invoke 类型由宿主明确识别，旧宿主不能被误称为兼容。
- 全量比较请求/响应字段及参数 Schema；字段数或工具数相同不等于契约兼容。
- 同一目标/profile、相同内存文件和准确工具内容得到确定性源码；不同 SDK/目标组合不能混用。
- 伪造绑定、遗漏读取、脏/过期缓冲区、被替换 Schema、非法视图/动作、只读和输出冲突全部拒绝。
- 插件不能通过通用桥接写入规避生成计划；正常取消、协议错误和原始工具诊断继续可观察。
- 初次生成、重复生成、删去旧输出、用户改过输出、已有工程接入和显式迁移均验证整体应用与保存恢复。

首个系列接入前，须准备其准确 profile Schema、支持矩阵、规则证据和真实生成编译夹具。本文的虚构 DEMO 数据只用于结构交流，不提供真实系列能力或硬件验收结论。
