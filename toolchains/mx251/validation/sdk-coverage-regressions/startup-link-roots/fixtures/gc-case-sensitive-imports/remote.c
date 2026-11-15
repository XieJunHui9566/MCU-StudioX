#include "common.h"

void Start(void)
{
    observed = 0x64;
}

unsigned char start(unsigned char value)
{
    return value ^ 0x53;
}
