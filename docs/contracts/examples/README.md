# 图形化契约结构示例

这里的 DEMO4、DEMO-4、框架及插件身份都是虚构的。它们只用于检查公共文件结构和 profile 叠加结构，不是可导入器件包、可安装插件、真实电气资料或生成源码。真实器件资料不得使用 `synthetic` 证据。

- `demo-source.txt`：虚构基础资料；示例文件中的证据摘要对应它的实际原始字节。
- `demo-profile-v1.schema.json`：对通过公共校验的完整文档执行的 profile 叠加 Schema，约束本例扩展字段与参数；它不会替代插件对启用/资源依赖的求值。
- `demo.template.mcuconfig`：包内初始文件，未分配 GPIO，保留明确的初始时钟参数；没有工程来源或生成凭据。
- `demo.project.mcuconfig`：创建后副本的结构示例，注入来源并保存用户把 `pad-3` 配为 GPIO 输出的修改；尚未生成源码，`generation` 仍为 `null`。

公共 Schema 为上级目录 `mcuconfig-v1.schema.json`，使用 Draft 2020-12。两个 `.mcuconfig` 文件都应通过公共和 profile Schema，Schema 文件本身应通过对应元 Schema。公共 Schema 不能检测同 ID 不同内容、引用不存在、证据真实性或电气冲突，这些要求详见主标准。

`dataSchema.sha256`、证据摘要、工程样例的 `templateSha256` 和 `definitionsSha256` 均可由随附数据复核。本例只有 ASCII 属性名、整数数值和普通 Unicode 字符串，适合独立核对 JCS 输出；对任意真实数据实现 JCS 时仍需完整遵循 RFC 8785，不能用普通排序 JSON 代替。

工程示例的 `packContentHash` 有意为 64 个 `0`：这里不存在对应完整器件包，该值只是绑定字段的结构占位，不通过真实工程来源核验。样例也没有对应真实插件，不能据此宣称通过插件语义校验、代码生成、编译或硬件验收。

后续准备真实器件包时，必须使用准确器件资料、可复核来源、实际包内容哈希、实际 profile Schema 和已实现插件；禁止将本例改名后直接用于真实型号。
