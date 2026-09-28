"""离线协议夹具：用 CPython 检查文件传输脚本；不代表 MicroPython 或实板验收。"""

import contextlib
import io
import json
import os
import sys
import traceback

# RP2 LittleFS 提供替换重命名与 sync；Windows 夹具显式模拟这两项语义。
os.rename = os.replace
os.sync = lambda: None
scope = {}
for line in sys.stdin:
    request = json.loads(line)
    output, error = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
        try:
            exec(request["code"], scope)
        except BaseException:
            traceback.print_exc()
    print(json.dumps({"output": output.getvalue(), "error": error.getvalue()}), flush=True)
