#include <stdint.h>

/* 把初值区域推到 0x0200 以上，覆盖空块、单字节、整页、跨页四个边界。
 * 此程序仅用于离线指令验证，不是可烧录的板级程序。 */
__xdata uint8_t padding[511];
volatile __xdata uint8_t test_result;
#if XINIT_CASE == 1
volatile __xdata uint8_t initialized[1]={0x66};
#elif XINIT_CASE == 2
volatile __xdata uint8_t initialized[256]={[0]=0x11,[255]=0x55};
#elif XINIT_CASE == 3
volatile __xdata uint8_t initialized[257]={[0]=0x11,[255]=0x55,[256]=0x66};
#endif
void main(void)
{
#if XINIT_CASE != 1
    uint16_t i;
#endif
#if XINIT_CASE == 1
    if (initialized[0] != 0x66) goto failed;
#elif XINIT_CASE == 2 || XINIT_CASE == 3
    if (initialized[0] != 0x11 || initialized[255] != 0x55) goto failed;
    for(i=1;i<255;++i) if (initialized[i]) goto failed;
#if XINIT_CASE == 3
    if (initialized[256] != 0x66) goto failed;
#endif
#else
    i=0;
    if (padding[i]) goto failed;
#endif
    test_result=0x5a;
    for (;;) { }
failed:
    test_result=1;
    for (;;) { }
}
