# STC ISP 便携运行时

`StcIspService` 在发行目录中查找 `runtime/stc-isp/Scripts/stcgal.exe`，并使用同目录上级的 `python.exe` 执行项目的型号防护脚本。编译开发环境组件 `stc.sdcc` 与这个下载运行时独立。

## 准备

运行时使用 [Python 3.14.7 官方 Windows x64 嵌入式包](https://www.python.org/downloads/release/python-3147/)，其 SHA-256 固定为 `d297e5ff019966817ad8502465176139f2d3d840fa4ed84b13bed399a6ab1f15`。构建脚本不会下载依赖，需事先取得官方 ZIP、本机安装的四个 Python 包，以及能构建 Windows x64 程序的 GCC。

```powershell
& .\tools\Prepare-StcIspRuntime.ps1 `
  -PythonEmbedArchive '.\artifacts\vendor\python-3.14.7-embed-amd64.zip' `
  -PackagesDirectory '<本机 Python site-packages 目录>' `
  -CompilerExecutable '<本机 C 编译器路径>'
```

脚本验证包版本为 stcgal 1.10、pyserial 3.5、tqdm 4.67.3、colorama 0.4.6，检查官方 ZIP 的固定哈希，只复制纯 Python 源文件和所需运行库，生成 `artifacts/tool-runtime/stc-isp-portable-3.14.7`。发行流程把这个目录复制为 `runtime/stc-isp`。`provenance.json` 记录来源、版本、元数据哈希、启动器源码哈希、编译器版本及逐文件 SHA-256。运行时约 13.3 MB。

pip 安装产生的 `Scripts/stcgal.exe` 可能把开发者的 Python 绝对路径写入启动器。这里的 `Scripts/stcgal.exe` 从自身路径定位相邻的 `python.exe`，不依赖机器上的 Python 或 `PATH`。Python 的 `python314._pth` 将导入路径限制在运行时目录，不加载用户站点包。串口程序的实际执行仍由 `StcIspService` 直接启动此目录的 `python.exe` 和 IDE 内置防护脚本。

## 离线核验

在一个含空格的新目录复制完整运行时，清除 `PYTHONHOME`、`PYTHONPATH`，并把 `PATH` 降到 Windows `System32` 后，执行：

```powershell
& '<新目录>\Scripts\stcgal.exe' -V
& '<新目录>\Scripts\stcgal.exe' --help
& '<新目录>\python.exe' -B -u '.\src\StudioX.Engine\Resources\studiox-stcgal-guard.py' --help
```

应显示 `stcgal 1.10`、帮助页和防护脚本的 `--expected-model` 参数。上述命令不连接 COM 口。2026-09-24 已在独立 `tmp/STC ISP portable final spaced path` 路径按此方法核验，还检查了有空格的参数转义和 Python `sys.path` 均成功；未进行下载或实板验证。

## 许可

运行时 `licenses/` 保留 Python PSF 协议、stcgal MIT、pyserial BSD-3-Clause、tqdm 上游 `LICENCE`、完整 MPL-2.0 及 colorama BSD-3-Clause。模块以 `.py` 源码形式再分发，未使用可选的 USB/PyUSB 功能。pyserial 许可文本取自[官方 v3.5 标签](https://github.com/pyserial/pyserial/blob/v3.5/LICENSE.txt)，MPL-2.0 文本取自 [Mozilla](https://www.mozilla.org/en-US/MPL/2.0/)；其他组件的版本与原始许可见运行时 `provenance.json` 和各源码头。正式公开发行前，仍应在发行件中核对这些文件完整随附。
