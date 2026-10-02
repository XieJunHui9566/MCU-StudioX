# Windows 安装与升级

版本从 `Directory.Build.props` 的 `ProductVersion` 读取；独立 IDE 当前源码与安装器目标版本为 **0.2.5.4A**，可覆盖升级首版 0.1.0，与旧 VS Code 插件的 0.17 分开。关于窗口、欢迎页、状态栏和安装器显示相同产品版本；Windows 文件与程序集版本为 **0.2.5.4**，产品版本保留 `A` 修订后缀。

## 构建

```powershell
.\tools\Prepare-InstallerCompiler.ps1
.\tools\Build.ps1
.\tools\Build-Installer.ps1
```

构建器固定使用 Inno Setup 7.1.0 x64，准备脚本检查下载 SHA-256 和 Pyrsys B.V. 的有效数字签名。只在开发机器准备安装编译器；最终用户不需要 Inno Setup、.NET、VS Code 或开发工具环境。

`Build-Installer.ps1` 调用自包含发布、检查工具和器件包、生成逐文件 SHA-256，再压缩为单个 EXE。默认输出 `artifacts/releases/<version>/`，包括安装器、SHA256SUMS.txt、使用说明、release.json 和单独的 device-packs。已成功发行的目录不允许覆盖。用户明确要求同版本修复发布时，保持产品版本，使用新的输出目录和 Git 发布标签记录修复源码，不移动已有标签。

只有用户明确要求升级时才修改 `Directory.Build.props` 的产品版本。`Publish.ps1 -ReleaseVersion` 仅用于明确的发行构建；工具集、器件包和插件版本分别管理，不随 IDE 版本强行改变。不要修改已经发行的工具集版本内容，因为工程会锁定其指纹。

`Publish.ps1` 和 `Build-Installer.ps1` 支持 `-ExcludePlugins`：不构建或附带示例插件，保留插件宿主与通用扩展接口。已有 Payload 使用该参数时安装构建器会拒绝带有 `runtime/plugins` 的目录。安装器不会安装、更新或删除用户数据目录中的插件。

## 安装语义

- Windows 10/11 x64，按当前用户安装；默认 `%LOCALAPPDATA%\Programs\MCU StudioX`，可选择位置。
- 创建开始菜单的 IDE、器件包、使用说明和卸载入口，可选桌面快捷方式。
- 永久 AppId：`{B8050FBC-2DE2-43F4-B839-F0E90522E671}`。后续版本必须保留此值，否则无法正常识别旧版本。
- 新版安装包直接覆盖升级，同版本可以修复。比较版本后拒绝降级。升级禁止改变安装目录；迁移时先卸载再安装。
- IDE 运行期间保留 `MCUStudioX.Desktop.InstallLock` 命名互斥量，安装器要求先关闭 IDE；不强杀有未保存修改的窗口。
- 当前是离线完整安装包升级，没有联网自动更新地址或后台更新服务。
- 不修改全局 PATH、不安装 USB 驱动、不关联 `.mcupack` 默认打开程序、不修改用户工程。

## 数据和器件包

用户数据保存在 `%LOCALAPPDATA%\MCUStudioX`，包括主题、背景配置、编辑器设置、最近工程和已导入器件包。安装器不访问这些文件；卸载仅删除其自身记录的程序文件，没有递归清空安装目录的规则。工程仍位于用户选择的位置。

随附 `.mcupack` 按厂商位于安装目录的 `device-packs/` 下；另在发行输出目录保存一份，便于单独发送或更新。0.2.2 安装包收录 STM32、AGM、WCH、Puya、GigaDevice、STC 和 Raspberry Pi 的当前包，具体清单及 SHA-256 见 `device-packs/index.json`。器件包版本独立于 IDE 版本；各型号的实板验收范围见对应文档。

0.2.5 同时内置 Windows PC C/C++ 工具链、ESP-IDF 5.5.4、ESP8266 RTOS SDK 3.4 及七个 Espressif 器件包；SDK 由工程共享，不向每个工程复制。

## 验证

`tools/Test-Installer.ps1 -Installer <setup.exe> -OutputDirectory <new-directory>` 使用同 AppId 的 0.1.0 模拟旧安装，验证覆盖升级、同版本修复、降级拒绝、运行中拒绝、目录更换拒绝、逐文件完整性、自包含桌面启动和卸载保留数据。目标版本从安装器读取，可用 `-PreviousVersion` 指定模拟旧版本。如果当前用户已经安装了正式产品，必须传入 `-PayloadDirectory <完整发行目录>`：测试以相同安装脚本及完整 Payload 编译唯一测试 AppId 的安装器，不修改正式安装和卸载项。无后缀、A..Z 按顺序排列，数字部分优先比较；字母版本额外验证相同数字版本的降级拦截。

## 分发状态

目前未配置产品代码签名证书，成品安装包为未签名文件；SHA-256 可检查传输完整性，不能代替发布者数字签名。

现有 `runtime/THIRD-PARTY-NOTICES.txt` 明确记录：工具链来自本地既有发行副本，公开发行前还需要归档供应商的对应源码及再分发材料，尤其 AGM 修改版 GCC/OpenOCD。安装包保留现有许可证和来源记录，但制作安装器不等于补齐这些材料，也没有生成或冒充供应商源码承诺。正式对外分发前需补齐对应材料。

安装器参考：[Inno Setup 官方下载](https://jrsoftware.org/isdl.php)、[升级时保留 AppId](https://jrsoftware.org/isfaq.php)、[AppMutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm)。
