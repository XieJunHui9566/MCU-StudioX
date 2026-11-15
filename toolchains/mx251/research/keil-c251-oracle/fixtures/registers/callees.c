#include "probe.h"
u8 leaf8(u8 value) reentrant { return value + 1U; }
u16 leaf16(u16 value) reentrant { return value + 1U; }
u32 leaf32(u32 value) reentrant { return value + 1UL; }
