# Windows 安装与升级

版本从 `Directory.Build.props` 的 `ProductVersion` 读取；独立 IDE 当前源码与安装器目标版本为 **0.2.6LTS**，可覆盖升级 0.2.6.10、0.2.5.4B 及首版 0.1.0，与旧 VS Code 插件的 0.17 分开。关于窗口、欢迎页、状态栏和安装器显示 **0.2.6LTS**；Windows 文件与程序集使用固定数字版本 **0.2.6.11**。发布脚本和安装器共用这一身份映射，数字相同时 LTS 名称高于无后缀版本；`0.2.6.12` 和后续系列仍可正常升级。

## 构建

```powershell
.\tools\Prepare-InstallerCompiler.ps1
.\tools\Build.ps1
.\tools\Build-Installer.ps1
```

构建器固定使用 Inno Setup 7.1.0 x64，准备脚本检查下载 SHA-256 和 Pyrsys B.V. 的有效数字签名。只在开发机器准备安装编译器；最终用户不需要 Inno Setup、.NET、VS Code 或开发开发环境组件。

`Build-Installer.ps1` 调用自包含发布、检查工具和器件包、生成逐文件 SHA-256，再压缩为单个 EXE。默认输出 `artifacts/releases/<version>/`，包括安装器、SHA256SUMS.txt、使用说明、release.json 和单独的 device-packs。已成功发行的目录不允许覆盖。用户明确要求同版本修复发布时，保持产品版本，使用新的输出目录和 Git 发布标签记录修复源码，不移动已有标签。

只有用户明确要求升级时才修改 `Directory.Build.props` 的产品版本。`Publish.ps1 -ReleaseVersion` 仅用于明确的发行构建；开发环境组件、器件包和插件版本分别管理，不随 IDE 版本强行改变。不要修改已经发行的开发环境组件版本内容，因为工程会锁定其指纹。

`Publish.ps1` 和 `Build-Installer.ps1` 支持 `-ExcludePlugins`：不构建或附带示例插件，保留插件宿主与通用扩展接口。已有 Payload 使用该参数时安装构建器会拒绝带有 `runtime/plugins` 的目录。安装器不会安装、更新或删除用户数据目录中的插件。

## 安装语义

发布脚本支持 `-DistributionProfile full|light`，默认 full。两种版本保留相同自包含 IDE、器件包、内置插件、clangd、Git 和独立宿主；完整版预装所有当前开发工具，轻量版不含 MCU/HDL/PC 开发环境组件和独立下载运行环境。完整安装器为 `MCU-StudioX-<version>-win-x64-Setup.exe`，轻量安装器为 `MCU-StudioX-<version>-win-x64-Light-Setup.exe`。旧 base 参数及 `Publish-Base.ps1` 是 light 的兼容入口。

`Publish-Light.ps1 -OutputDirectory <新目录>` 可直接生成轻量目录。`Build-Installer.ps1 -PayloadDirectory <目录> -DistributionProfile light -OutputDirectory <新目录>` 核对载荷配置后打包；配置不符或轻量目录带工具时拒绝。`-DistributionCatalogDirectory` 可带入校验过的离线工具/插件/组件目录；不会自动安装条目或启用插件。两种版本都支持“导入开发环境组件…”导入 `.mcutoolchain`，旧 `.studioxtools` 格式 1 仍受支持。流程及验证边界见 [产品工作流](PRODUCT_WORKFLOWS.md)。

- Windows 10/11 x64，按当前用户安装；默认 `%LOCALAPPDATA%\Programs\MCU StudioX`，可选择位置。
- 创建开始菜单的 IDE、器件包、使用说明和卸载入口，可选桌面快捷方式。
- 永久 AppId：`{B8050FBC-2DE2-43F4-B839-F0E90522E671}`。后续版本必须保留此值，否则无法正常识别旧版本。
- 新版安装包直接覆盖升级，同版本可以修复。比较版本后拒绝降级。升级禁止改变安装目录；迁移时先卸载再安装。
- 完整版和轻量版在同一安装位置互换，程序文件按正常升级处理。工具按 `ID/版本` 整个目录保留；只有目录不存在时才安装预装版本。损坏、内容不同或不完整的已有目录也不会被合并或覆盖，应进入 IDE 的“工具校验与修复”。后来导入的版本、恢复目录、工程版本与内容锁保持不变。
- 预装及后来导入的开发工具、旧 HDL/STC 下载运行环境由组件管理负责，卸载 IDE 不删除它们。目的地运行目录或组件路径含目录链接时拒绝安装，避免写入别处。
- IDE 运行期间保留 `MCUStudioX.Desktop.InstallLock` 命名互斥量，安装器要求先关闭 IDE；不强杀有未保存修改的窗口。
- 当前是离线完整安装包升级，没有联网自动更新地址或后台更新服务。
- 不修改全局 PATH、不安装 USB 驱动、不关联 `.mcupack` 默认打开程序、不修改用户工程。

## 数据和器件包

用户数据保存在 `%LOCALAPPDATA%\MCUStudioX`，包括主题、背景配置、编辑器设置、最近工程和已导入器件包。安装器不访问这些文件；卸载仅删除当前安装记录的程序文件，没有递归清空安装目录的规则。工程仍位于用户选择的位置。

为迁移旧安装器对工具文件的卸载所有权，新安装使用 `UninstallLogMode=overwrite`，组件文件使用 `uninsneveruninstall`。旧完整版升级到轻量版后再卸载，旧工具文件也会保留。该方式会让已从新载荷移除的历史程序文件不再被新卸载记录管理，卸载后可能留下这些文件；本轮不递归清理它们。规则依据 [Inno Setup UninstallLogMode](https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm) 和 [Files flags](https://jrsoftware.org/ishelp/topic_filessection.htm)。

随附 `.mcupack` 按厂商位于安装目录的 `device-packs/` 下；另在发行输出目录保存一份，便于单独发送或更新。0.2.2 安装包收录 STM32、AGM、WCH、Puya、GigaDevice、STC 和 Raspberry Pi 的当前包，具体清单及 SHA-256 见 `device-packs/index.json`。器件包版本独立于 IDE 版本；各型号的实板验收范围见对应文档。

完整版同时预装 Windows PC C/C++ 开发环境组件、ESP-IDF 5.5.4、ESP8266 RTOS SDK 3.4；两种版本都随附七个 Espressif 器件包。轻量版导入对应开发环境组件后使用共享 SDK，不向每个工程复制。

## 验证

`tools/Test-Installer.ps1 -Installer <setup.exe> -OutputDirectory <new-directory>` 使用同 AppId 的 0.1.0 模拟旧安装，验证覆盖升级、同版本修复、降级拒绝、运行中拒绝、目录更换拒绝、逐文件完整性、自包含桌面启动和卸载保留数据。目标版本从安装器读取，可用 `-PreviousVersion` 指定模拟旧版本。如果当前用户已经安装了正式产品，必须传入 `-PayloadDirectory <完整发行目录>`：测试以相同安装脚本及完整 Payload 编译唯一测试 AppId 的安装器，不修改正式安装和卸载项。无后缀、A..Z 按顺序排列，数字部分优先比较；字母版本额外验证相同数字版本的降级拦截。

正式分发 EXE 的来宾工作流由 `tools/acceptance/Test-ShippingInstaller.ps1` 验证，采用明确的输入清单、指定 VMware 来宾和私有目录；发现已有产品安装即停止。当前矩阵覆盖 0.2.6.10 轻量首装、离线 ARM 选装、首个工程 WPF 引导、完整/轻量切换、同版本修复、安装保护、卸载保留，以及实际 0.2.5.4B 完整版升级到 0.2.6.10。ARM 与 ESP32-C3 构建调用安装目录中的真实程序集和工具，不依赖工作区编译出的产品核心；验证程序自身为独立自包含程序。

`Start-ShippingInstallerGuest.ps1` 在 Windows PowerShell 5.1 下从共享目录核对 SHA-256 后复制到来宾磁盘运行，保存已完成阶段并接收宿主的续跑或清理请求。`Verify-ShippingInstallerEvidence.ps1` 在宿主逐文件核验回传 ZIP 的清单、哈希、阶段结果、程序集来源、构建和数据保留证据；通过后才能发送匹配该 ZIP 哈希的清理请求。来宾 ZIP 已删除时仍可通过宿主保留证据继续清理，不覆盖旧证据或重复计算已删除字节。这套入口需要明确准备的计划与载荷清单，不作为普通用户的安装步骤。

2026-10-05 上述 14 阶段已在指定 Windows 11 来宾全部通过，234 个证据文件经宿主独立 SHA-256 核验，8 份安装服务报告和 16 份保留报告均通过。F407、F103 与 ESP32-C3 首次构建实际编译；升级后的旧工程再次执行配置和增量构建，Ninja 报告无需重编。三项安装保护已核对原始拒绝原因，持久 PATH 与所检查的开发环境变量保持一致。证据和范围见 [P0 验收说明](P0_ACCEPTANCE.md)。

## 分发状态

`tools/Test-DistributionProfiles.ps1 -OutputDirectory <新目录> -DesktopExe <实际桌面EXE>` 用相同安装脚本、唯一测试 AppId 和微型工具载荷检查完整/轻量互换、组件整目录保留、不完整版本、目录链接拒绝、卸载和旧卸载所有权迁移。不会执行测试工具；正式安装及其注册项保持不变。`Test-DistributionPayloads.ps1` 对真实发行目录核对共同文件、器件包、组件清单及轻量版裁剪边界；轻量启动另行验证。0.2.6.10 以完整和轻量两种安装包发行，验证记录随 GitHub Release 提供。

完整 Payload 的隔离验收可加 `-NoCompression`，避免重复压缩 SDK；测试安装器使用旁置数据文件，仍验证相同安装逻辑和逐文件哈希，不能作为正式发行包上传。正式安装包保持默认压缩。旁置文件处理参照 [Inno Setup DiskSpanning](https://jrsoftware.org/ishelp/topic_setup_diskspanning.htm)。

目前未配置产品代码签名证书，成品安装包为未签名文件；SHA-256 可检查传输完整性，不能代替发布者数字签名。

现有 `runtime/THIRD-PARTY-NOTICES.txt` 明确记录：开发环境组件来自本地既有发行副本，公开发行前还需要归档供应商的对应源码及再分发材料，尤其 AGM 修改版 GCC/OpenOCD。安装包保留现有许可证和来源记录，但制作安装器不等于补齐这些材料，也没有生成或冒充供应商源码承诺。正式对外分发前需补齐对应材料。

安装器参考：[Inno Setup 官方下载](https://jrsoftware.org/isdl.php)、[升级时保留 AppId](https://jrsoftware.org/isfaq.php)、[AppMutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm)。

## 基础版首次工程

基础版用户先从新建工程页导入或同步所需器件包，明确选型并创建工程，然后进入“工具 → 开发环境组件 → 准备工程开发环境组件”安装工程锁定的开发环境组件。发布流程可用 Publish-Base.ps1 准备基础 Payload；此入口不自动建立公共目录、下载 SDK 或升级 IDE。工作流和验证见 [准备工程开发环境组件](ON_DEMAND_TOOLS.md)。
