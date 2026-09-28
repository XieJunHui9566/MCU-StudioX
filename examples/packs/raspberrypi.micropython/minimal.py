"""有限次输出，方便首次通过 REPL 检查解释器与脚本上传。"""

import gc
import machine
import sys
import time

print(sys.implementation)
print("CPU Hz:", machine.freq())
for count in range(5):
    print("Hello MicroPython", count, "free heap:", gc.mem_free())
    time.sleep_ms(200)
