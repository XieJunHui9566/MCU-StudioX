#include "common.h"

volatile unsigned char observed;
volatile __xdata unsigned char initialized_bytes[5] = { 0x41, 0x02, 0x83, 0xa4, 0x15 };
volatile __xdata unsigned long initialized_word = 0x89abcdefUL;
volatile __xdata unsigned char bss_bytes[9];
volatile __data unsigned char data_bss;
const __code unsigned char copy_source[4] = { 0x10, 0x32, 0x54, 0x76 };
volatile __xdata unsigned char copy_target[4];

/* 必须能删除无调用函数，同时保留初始化运行库的纯声明拉库契约。 */
void unused_initializer_path(void)
{
    observed = 0x64;
}

void main(void)
{
    unsigned char index;
    observed = initialized_bytes[0] ^ (unsigned char) initialized_word ^ bss_bytes[0] ^ data_bss;
    for (index = 0; index < 4; ++index)
    {
        copy_target[index] = copy_source[index];
    }
    while (1) { }
}
