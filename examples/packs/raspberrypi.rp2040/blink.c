#include "pico/stdlib.h"

// 官方 Pico/Pico H 的板载 LED 使用 GP25；其它兼容板须先核对原理图。
#define LED_PIN PICO_DEFAULT_LED_PIN

int main(void)
{
    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);
    for (;;)
    {
        gpio_put(LED_PIN, true);
        sleep_ms(500);
        gpio_put(LED_PIN, false);
        sleep_ms(500);
    }
}
