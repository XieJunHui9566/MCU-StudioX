#include "pico/stdlib.h"
#include "pico/multicore.h"
#include "pico/util/queue.h"

static queue_t requests;
static queue_t responses;
volatile uint32_t app_counter;
volatile uint32_t core1_result;

static void core1_entry(void)
{
    for (;;) {
        uint32_t input;
        queue_remove_blocking(&requests, &input);
        uint32_t result = input * 3u + 1u;
        queue_add_blocking(&responses, &result);
    }
}

int main(void)
{
    // SDK 队列负责跨核同步，不用 volatile 变量代替同步机制。
    queue_init(&requests, sizeof(uint32_t), 1);
    queue_init(&responses, sizeof(uint32_t), 1);
    multicore_launch_core1(core1_entry);
    for (;;) {
        uint32_t input = ++app_counter;
        uint32_t result;
        queue_add_blocking(&requests, &input);
        queue_remove_blocking(&responses, &result);
        core1_result = result;
        sleep_ms(100);
    }
}
