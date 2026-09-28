# MicroPython 脚本工程

板型和解释器版本由 `.studiox/project.json` 明确记录。Pico / Pico H 使用 RPI_PICO；Pico 2 使用 RPI_PICO2 的 ARM 固件。本模板不适用于 W 无线板或任意兼容板配置。

1. 首次使用需通过官方 BOOTSEL 步骤安装 **MicroPython 1.29.0**。IDE 的“工具 → MicroPython”提供对应官方固件页；请选择 1.29.0 稳定版。安装解释器前自行保存板上原固件与数据，IDE 不自动安装或升级解释器。
2. 打开“工具 → MicroPython · REPL 与脚本上传”，选择开发板 USB COM 端口并连接。连接会发送 Ctrl-C 中断当前脚本，随后核对解释器报告的板型与版本。
3. 保存 `main.py` 后输入相对文件名，点击“上传已保存脚本”。上传同名文件前将原内容备份到本工程 `.studiox/micropython-backups/`，分块传输并校验 SHA-256；上传不会自动运行脚本。
4. REPL 片段可输入 `exec(open('main.py').read())` 执行入口。表达式使用 `print` 显示结果。单次 REPL 操作最多 30 秒，运行循环可点击“停止 / 断开”；若希望脱机启动，请上传后按开发板复位键或重新上电。

`boot.py` 默认保留 USB REPL；只有修改启动设置时才需手动上传它。`lib/` 可放置自己的 `.py` 模块，逐文件上传。上传不会同步删除板上其他文件。文件最大 256 KiB，目录和文件名不能包含上级路径或隐藏目录。

F7/GCC、OpenOCD 与 GDB 不用于这个工程。C SDK 模板仍可在同一器件包中另外选择。当前 API 提示基于固定版本文档和显式导入，不能代替板上运行验证。

来源：[MicroPython RP2 1.29.0](https://docs.micropython.org/en/v1.29.0/rp2/quickref.html)、[REPL 协议](https://docs.micropython.org/en/v1.29.0/reference/repl.html)。
