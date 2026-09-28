#include "pico/stdlib.h"

volatile uint32_t app_counter;

int main(void)
{
    // Pico SDK 启动代码按官方 Pico 配置初始化 12 MHz 晶振和 125 MHz 系统时钟。
    for (;;)
    {
        ++app_counter;
        sleep_ms(100);
    }
}
