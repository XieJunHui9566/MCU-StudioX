# 第三方组件与素材

根目录的 MIT 许可证仅适用于本项目自有源码。仓库中的第三方文件继续遵守各自原始许可证、著作权与商标规则；本项目不对其重新授权。

- 厂商名称和标识仅用于识别对应器件厂商，不表示厂商背书。相关素材的来源与转换方式见 `licenses/Manufacturer-Logos-NOTICE.md`，著作权与商标权归各自权利人，不适用本项目 MIT 许可。
- `licenses/` 保存本项目依赖或可选运行组件的许可证文本；`runtime/THIRD-PARTY-NOTICES.txt` 记录运行时与工具链来源及相关义务。
- 构建脚本可能使用另行取得的厂商 SDK、编译器、OpenOCD、GDB、Git 等完整工具发行。它们不随此源码快照一并发布，其复制、修改和再分发仍需符合各自许可证。
- `src/StudioX.Engine/Resources/Ag32/Peripherals/` 包含本次适配所需的 AGM 原始驱动与模拟 IP 资源。它们不适用本项目 MIT 许可，版本、SHA-256 与原厂许可说明见该目录的 `provenance.json` 和 `NOTICE.md`。
- `.mcupack` 中若包含厂商头文件、启动代码、库或其他 SDK 文件，以该包内的来源记录与原许可为准；不能仅凭本仓库 MIT 许可证推断其可再分发范围。
