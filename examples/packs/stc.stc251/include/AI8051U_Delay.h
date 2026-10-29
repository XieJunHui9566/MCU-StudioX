#ifndef AI8051U_DELAY_H
#define AI8051U_DELAY_H

#include <stdint.h>

/* 阻塞延时，单位 ms，0 立即返回，范围 0..65535。
 * IRC 频率来自工程详情，修改后重新编译下载；不占用定时器。
 * 适用于 IRC 作为系统时钟且程序不另改 CLKDIV/PLL 的 32 位模式。
 * 中断及调用开销会延长实际时间；精密时序请使用硬件定时器。
 * 实现临时使用 WTST=0，返回时恢复；不改变中断开关或端口。 */
void Delay_MS(uint16_t milliseconds) __sdcccall(2);

#endif
