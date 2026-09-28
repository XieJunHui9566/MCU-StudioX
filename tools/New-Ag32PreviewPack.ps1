param([string]$SdkDirectory, [string]$OutputFile)

# 保留旧命令名以给出迁移说明，避免历史自动化再次生成同 ID 的旧单型号包。
throw 'AG32 单型号预览包生成入口已停用。请使用 tools/New-Ag32Packs.py，通过 --sdk-directory、--platform-directory、--output 和 --cli 生成当前四个子系列包；仅准备源码可加 --stage-only。参数说明见 examples/packs/agm.ag32-series/README.md。'
