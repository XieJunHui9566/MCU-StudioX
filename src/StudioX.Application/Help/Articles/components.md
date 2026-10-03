# 工程组件安装、锁定与回退

“工具 → 插件与扩展 → 软件与组件分发…”可从目录选择组件，或点击“导入本地组件”打开 .studioxcomponent。组件是带许可证和完整文件哈希的源码归档，不执行安装脚本，不附带可执行 CMake 插件。

## 加入工程

预览名称、ID、版本、许可证、来源、源码和头文件目录；原生 CMake 工程在“组件 CMake 目标”填写已有目标名称，默认 firmware。确认后保存编辑器源码并安装；构建或调试期间禁止修改组件配置。

版本源码保存在 studiox-components/<ID>/<版本>，studiox-components.lock.json 记录当前精确版本、文件哈希及最近 10 次变更。更新不会覆盖旧版本源码。

原生工程生成 studiox-components.cmake，并在根 CMakeLists.txt 末尾追加受控 include，用 target_sources 和 target_include_directories 加入所选目标。已有 CMake 内容保留。

## ESP-IDF 最小构建

ESP-IDF 工程生成 components/studiox_<ID>/CMakeLists.txt 注册源码和头文件。使用 MINIMAL_BUILD 时，需要在 main 的 idf_component_register 中添加对应 REQUIRES，例如 studiox_studiox_byte-utils；以生成目录名称为准。组件中的普通 C/C++ 源码不替代芯片 SDK。

## 查看、更新和回退

“查看工程组件”显示锁定信息。选择同 ID 的新版本并确认即可更新；“回退组件”恢复上次锁定集合并重新生成受控配置，然后需要重新构建。

## 拒绝覆盖与排错

不要手工修改版本源码或生成的 CMake：服务会检测外部修改并拒绝覆盖。确需自维护时先保存副本，按普通源码方式加入自己的目标。无效哈希、越界路径、符号链接、同名非托管文件或 ESP 组件名称冲突会被拒绝。
