#include "pico/stdlib.h"

// Pico 2 的板载 LED 使用 GP25；兼容板请按原理图修改此处。
#define LED_PIN 25u

int main(void)
{
    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);
    for (;;) {
        gpio_put(LED_PIN, true);
        sleep_ms(500);
        gpio_put(LED_PIN, false);
        sleep_ms(500);
    }
}
