# AI 助手重做与 DeepSeek 输入优化

日期：2026-10-30。产品版本保持 0.2.6.15。本轮完成源码开发与隔离验证，没有替换安装版、制作安装包、提交 Git、使用付费模型 API 或操作硬件。

## 完成内容

| 请求 | 实现 |
|---|---|
| 键盘发送 | Enter 发送，左 Shift+Enter 换行；中文输入法候选确认保护、按住 Enter 去重、选区替换与光标恢复 |
| 技能导入 | 聊天区「✦ 技能」，支持选择 SKILL.md 或技能目录；保留引用，默认提供并选中 mcu-code-style；技能按需读取 |
| 自定义 MCP | 独立设置页，可添加、编辑、测试、启用/禁用、删除；接入 Streamable HTTP、SSE 和本地 stdio；Bearer 凭据由 Windows 管理 |
| 调整方向 | 取消正在等待的模型生成，丢弃尚未执行的工具调用；已开始的工具批次完成后处理；方向消息保留在会话中 |
| API 范围 | 设置顶部明确仅支持 OpenAI 兼容 Chat Completions（包括 DeepSeek），不支持 Anthropic Messages；旧的不兼容地址仍能进入设置修正 |
| 内置 MCP | 增加 Arduino/PIC 预检与授权下载及自定义服务的 7 个工具；原有全部工具契约保持一致；移除系统提示中固定的 ESP-IDF 版本假设 |
| DeepSeek 优化 | 固定小型工具前缀、按需工具 schema、稳定消息前缀、JSON 无损压缩、较早的历史批次压缩并保留最近完整结果 |
| 清理 | 清除本轮隔离构建、格式化副本和重复预览；保留源码备份、修改补丁、验证日志及最终截图，不删除用户工程、历史和设置 |

## DeepSeek 测量与边界

固定离线工程包含 97 个可发现工具。工具定义序列化载荷从 **92,774 字节降至 7,749 字节，减少 91.6%**，模型常驻定义从 97 个减至 8 个。桌面编辑器会增加常用编辑工具，此样例不代表所有工程的相同幅度。

这项数字只衡量工具定义，不是整个请求的 token 数、模型质量或账单节省比例。按需检索会增加少量工具轮次；复杂任务仍需读取必要源码和诊断。保留最新完整工具批次，已用 180 次真实 MCP 调用的长任务验证，避免提前压缩丢失当前结果。后续源码安全检查将可选 QMD 后端改为内置 C# BM25，工具名保持兼容，描述文字有所调整；上述字节数保留为重做阶段的测量记录。

DeepSeek 服务端自动进行前缀缓存，命中是尽力提供。客户端保留稳定的系统、技能目录、工具定义和已选历史前缀；只按服务端返回的 prompt/cache hit/cache miss 展示用量。中断请求可能没有返回 usage，不能计为零，也不保证 HTTP 取消会停止服务端计费。没有增加 OpenAI 专用缓存参数或虚构 DeepSeek 显式缓存句柄。

研究资料：

- [DeepSeek 官方缓存规则](https://api-docs.deepseek.com/guides/kv_cache/)：自动前缀缓存、命中/未命中字段与尽力提供的边界。
- [DeepSeek 官方 harness 的系统提示组装](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/subsystems/system-prompt.md)：静态提示与动态上下文的组装、保留历史与增量上下文。
- [DeepSeek-V3 显式缓存提议](https://github.com/deepseek-ai/DeepSeek-V3/issues/1655)：属于功能请求，不能据此实现并未正式提供的 API。
- [MCP 官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)：使用仓库已锁定的原生 C# SDK 传输，不引入 Node 桥。

## 验证

| 检查 | 结果 |
|---|---|
| tools/Build.ps1 全量 Release 构建 | 0 警告、0 错误 |
| StudioX.AiValidation | 158 项通过；包含方向取消、未开始调用的丢弃、技能导入、工具按需发现、真实本机 HTTP/stdio MCP、禁用与变更地址 |
| StudioX.McpValidation | 157 项通过；原有契约、权限、配对、长任务、PDF、错误传播和清理 |
| WPF 隔离预览 | 8 项通过；键盘策略、选区换行、真实菜单共用的 MCP 设置服务及明暗主题 |
| 修改文件结构格式检查 | 0 结构变化；空白排版保留字面量 |

合计 **323 项检查通过**。GUI、AI 测试、MCP 测试使用相同 SHA-256 的 Application 程序集：`2E5FB24564080C453A1897FF2CB8A8C791C71C2C41BBE519C52B4D6D20089037`。

本机 HTTP 与 stdio 使用测试夹具；未验证用户尚未配置的外部服务。SSE 已按 SDK 接入，尚未单独运行旧式 SSE 服务验收。自定义服务当前只转发工具文本，不提供通用 OAuth、远端资源、提示词或图像转发。中文输入法测试覆盖事件策略，未声称完成真实输入法的原生交互验收。

证据保存于 `.artifacts/ai-assistant-redesign-20261030/`：`build-final.log`、`ai-validation.log`、`mcp-validation.log`、`ui-final/ai-redesign-checks.txt`、`ui-final/`、`source-before.zip`、`source-after.zip`、`changes.patch` 与 `result.json`。使用说明见 [AI_AGENT.md](AI_AGENT.md)。
