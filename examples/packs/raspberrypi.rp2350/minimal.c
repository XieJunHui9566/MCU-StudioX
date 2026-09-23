#include "pico/stdlib.h"

volatile uint32_t app_counter;

int main(void)
{
    // SDK 启动代码已完成 12 MHz 晶振、150 MHz 系统时钟和 C 运行时初始化。
    for (;;) {
        ++app_counter;
        sleep_ms(100);
    }
}
