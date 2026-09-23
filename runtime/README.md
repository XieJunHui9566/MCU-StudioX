# 发行时注入的资源

源码仓库不提交编译器、OpenOCD、厂商 SDK 或 .NET 二进制。本目录描述发行结构；完整工具资源由本机已准备的工具整理至 `artifacts/tool-runtime`，开发构建与发行使用同一布局。

代码提示迭代已内置独立的 clangd 22.1.6（约 50 MB 可执行文件及 LLVM 内建头文件），并从本机已有 AgRV 工具链准备 newlib 4.1.0 标准 C 头文件。它们用于语言分析，不包含固件编译/下载程序。普通用户随 IDE 一次安装，不安装编辑器插件。

```text
MCU StudioX.exe
runtime/
  git/cmd/git.exe
  git/mingw64/...
  git/usr/...
  git/LICENSE.txt
  git/studiox-provenance.json
  languages/
    clangd/bin/clangd.exe
    clangd/lib/clang/22/include/...
    clangd/LICENSE.TXT
    sysroots/agrv-gcc-11.1.0/include/...
    sysroots/agrv-gcc-11.1.0/manifest.json
    sysroots/agrv-gcc-11.1.0/Newlib-COPYING.txt
  toolsets/<id>/<version>/
    toolset.json
    gcc/...
    cmake/...
    ninja/...
    openocd/...
    licenses/...
  plugin-host/StudioX.PluginHost.exe
  plugins/<id>/plugin.json
  plugins/<id>/*.dll
  THIRD-PARTY-NOTICES.txt
```

`tools/Prepare-GitRuntime.ps1` 准备固定版本 Portable Git for Windows 2.55.0.windows.5，校验官方归档 SHA-256，执行上游便携初始化，移除复制进来的开发机网络配置。完整 Git、SSH、Git Credential Manager、Git LFS 和许可证随构建/发布复制到 `runtime/git`；准备脚本依赖开发机的 7-Zip，终端用户不需要它。发行与安装包构建都会拒绝缺少 Git 的资源目录。

`tools/Publish.ps1` 默认将 `artifacts/tool-runtime/toolsets` 装入发行目录，也可用 `-RuntimeAssetsDirectory <prepared-runtime>` 指定开发资源目录。当前包含 `agm.agrv`、`arm.gnu`、`riscv.xpack`，版本均为 `1.0.0`，具体组件版本和准备命令见 [工具说明](../docs/TOOLCHAINS.md)。工具集内的可执行程序、运行库、头文件、标准库、脚本和许可证完整保留，并全部列入 `sha256` 索引。工具角色和资源目录使用相对路径；不能依靠 PATH 或把某一版 RISC-V GCC 视作适用于所有 RISC-V 芯片。

生产发行必须预装其支持器件所需工具集，普通用户不承担工具准备工作。芯片包引用这些精确工具集；缺失时报告缺少组件，不弹出工具路径配置向导。首次发行阶段先随安装包分发完整工具集，后续可设计 IDE 内部的受校验组件更新。

语言组件构建资源放在忽略提交的 `artifacts/language-runtime`。`tools/Prepare-LanguageServer.ps1` 使用 [clangd 官方发行包](https://github.com/clangd/clangd/releases/tag/22.1.6)，固定 SHA-256 `ce54f16e0b4fd76d450eeda9664420b195360b73febcfe40e661108fa57f2ce1`；保留发行包自带许可证。`tools/Prepare-Ag32LanguageHeaders.ps1 -ToolchainDirectory <已准备的 AgRV GCC 11.1.0 根目录>` 复制标准 C 头文件、保留文件内版权，并生成逐文件哈希清单，附带 [newlib 版权与许可说明](https://sourceware.org/newlib/COPYING.NEWLIB)。不复制 `c++` 标准库目录。

Desktop 项目自动将以上资源复制到构建和发布输出；缺少 clangd 时发布脚本拒绝生成不完整的 IDE。语言解析参数通过 [clangd 编译命令扩展](https://clangd.llvm.org/extensions#compilation-commands) 传入进程，不生成或运行编译命令。用户工程里的自定义 `.clangd` 和环境 PATH 不用于当前配置。
