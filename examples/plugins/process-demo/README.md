# Python、Rust、C++ 进程插件

这三个示例使用 API 2 JSON Lines 协议，交付入口为插件目录内的固定 EXE。详见 [通用插件开发](../../../docs/PLUGINS.md)。标准输出只写协议，开发日志写标准错误。示例不连接设备、不烧录和不改工程文件。

## Python

Python 的标准库协议实现见 [plugin.py](../python/plugin.py)。开发和交付时自带便携 Python 发行版（含许可证与全部运行库），或冻结为 EXE。`plugin.json` 中的 `entryExecutable` 必须指向归档中的 EXE，脚本参数必须为被 SHA-256 索引保护的相对路径。StudioX 不调用系统任意 Python 路径、不下载运行时。

典型发布目录：

```text
payload/
  plugin.json
  plugin.py
  python/
    python.exe
    python3.dll
    python312.dll
    python312.zip
    python312._pth
    LICENSE.txt
```

示意文件名应替换为开发者实际选用发行版的文件，不猜测 Python 版本或移除必要依赖。`arguments` 使用 `["-I", "-u", "plugin.py"]`。发布目录准备好后使用 `StudioX.Cli.exe plugin pack`，完整索引以实际文件为准。

## Rust

`rust/` 提供完整 `Cargo.toml`、原生 EXE 协议代码和清单模板。使用已安装的 Rust 工具链及本地 Cargo 依赖缓存：

```powershell
cargo build --release --offline --manifest-path ./rust/Cargo.toml
```

将 `studiox-rust-overview.exe` 和必要运行依赖复制到独立发布目录，将 `plugin.template.json` 复制为 `plugin.json` 后打包。依赖缓存缺失时先由开发者按自身流程准备，不自动联网下载工具链。

## C++

`cpp/` 提供 C++20 原生 EXE、CMake 和清单模板。需要开发者已有编译器、CMake 与本地 `nlohmann_json` 3.11；CMake 不使用 FetchContent，也不下载依赖。

```powershell
cmake -S ./cpp -B ./cpp-build -DCMAKE_PREFIX_PATH=<本机JSON库安装目录>
cmake --build ./cpp-build --config Release
```

将 `studiox-cpp-overview.exe` 和所需运行依赖复制到独立发布目录，再用模板生成 `plugin.json` 并打包。Rust/C++ 示例通过白名单 `project_info` 读取当前工程，命令与 Agent 工具共用协议；短时任务读取双向取消，长时任务需扩展为专门读取循环和可取消工作队列。

当前已提供源码模板和统一协议支持；本仓库未自动安装或验证所有 Rust/C++ 开发工具链。插件安装后的启用由用户显式信任，进程仍具有当前用户权限。
