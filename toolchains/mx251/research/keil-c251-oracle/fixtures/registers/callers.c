#include "probe.h"
u8 call8(u8 live, u8 argument) reentrant { return leaf8(argument) + live; }
u16 call16(u16 live, u16 argument) reentrant { return leaf16(argument) + live; }
u32 call32(u32 live, u32 argument) reentrant { return leaf32(argument) + live; }
u32 second_caller(u32 live, u32 argument) reentrant
{
    return leaf32(argument) ^ (live + 0x12345678UL);
}
