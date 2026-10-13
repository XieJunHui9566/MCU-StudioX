#ifndef STUDIOX_RT51_CONFIG_H
#define STUDIOX_RT51_CONFIG_H

/* 3 个用户任务 + 1 个空闲任务。上下文含寄存器和返回地址，不能全部用作局部变量。 */
#ifndef RT51_MAX_TASKS
#define RT51_MAX_TASKS 4
#endif
#ifndef RT51_MAX_TIMERS
#define RT51_MAX_TIMERS 4
#endif
#ifndef RT51_ENABLE_STATS
#define RT51_ENABLE_STATS 1
#endif
#define RT51_STACK_BYTES 96
#define RT51_STACK_BASE 0x80
#define RT51_KERNEL_STACK_BASE 0x30
#define RT51_KERNEL_STACK_BYTES 80
#define RT51_QUEUE_ITEM_MAX 16
#define RT51_VERSION "0.1.0"

#if RT51_MAX_TASKS < 2 || RT51_MAX_TASKS > 8
#error RT51_MAX_TASKS must be between 2 and 8
#endif
#if RT51_MAX_TIMERS < 0 || RT51_MAX_TIMERS > 8
#error RT51_MAX_TIMERS must be between 0 and 8; 0 removes the timer service
#endif
#if RT51_ENABLE_STATS != 0 && RT51_ENABLE_STATS != 1
#error RT51_ENABLE_STATS must be 0 or 1
#endif
#if RT51_STACK_BYTES < 32 || RT51_STACK_BYTES > 128
#error RT51_STACK_BYTES must be between 32 and 128
#endif
#if RT51_STACK_BASE != 0x80 || RT51_KERNEL_STACK_BASE != 0x30 || RT51_KERNEL_STACK_BYTES != 80
#error Changing the IRAM layout requires adapting and validating the assembly port
#endif
#endif
