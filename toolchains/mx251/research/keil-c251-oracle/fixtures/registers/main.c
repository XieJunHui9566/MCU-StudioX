#include "probe.h"
volatile u32 observed;
void main(void)
{
    for (;;) {
        observed = call8(3U, 4U) + call16(1234U, 5678U);
        observed += call32(0x12345678UL, 0x89abcdefUL);
        observed ^= second_caller(0x12345678UL, 0x89abcdefUL);
    }
}
