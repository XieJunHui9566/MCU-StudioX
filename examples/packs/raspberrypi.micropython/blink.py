"""仅适用于官方 Pico / Pico 2 的 LED 配置，兼容板需先核对原理图。"""

from machine import Pin
from time import sleep_ms

led = Pin("LED", Pin.OUT)
try:
    while True:
        led.toggle()
        sleep_ms(500)
finally:
    # Ctrl-C 中断时关闭 LED，不关闭 USB REPL。
    led.off()
