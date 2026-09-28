/*
 * 正点原子探索者 V3.5 Zephyr 实验模板。
 * PF9/LED0 来自文件名为 V3.5、但页内修订栏为 V3.4 的原理图；
 * 已在所测 V3.5 实板上通过闪烁观察与 GPIO 寄存器核验。
 * SPDX-License-Identifier: Apache-2.0
 */

#include <stdbool.h>

#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/printk.h>

#define LED0_NODE DT_ALIAS(led0)

#if !DT_NODE_EXISTS(LED0_NODE)
#error "Board must define the led0 alias"
#endif

static const struct gpio_dt_spec led = GPIO_DT_SPEC_GET(LED0_NODE, gpios);

int main(void)
{
    if (!gpio_is_ready_dt(&led)) {
        printk("LED0 GPIO device is not ready\n");
        return 0;
    }

    if (gpio_pin_configure_dt(&led, GPIO_OUTPUT_INACTIVE) < 0) {
        printk("LED0 GPIO configuration failed\n");
        return 0;
    }

    printk("Alientek Explorer STM32F407ZG: experimental Zephyr board\n");
    while (true) {
        if (gpio_pin_toggle_dt(&led) < 0) {
            printk("LED0 GPIO toggle failed\n");
            return 0;
        }
        k_sleep(K_MSEC(500));
    }

    return 0;
}
