/* Single-purpose compiler observations, not a performance benchmark. */
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned long u32;
u8 leaf8(u8 value) reentrant;
u16 leaf16(u16 value) reentrant;
u32 leaf32(u32 value) reentrant;
u8 call8(u8 live, u8 argument) reentrant;
u16 call16(u16 live, u16 argument) reentrant;
u32 call32(u32 live, u32 argument) reentrant;
u32 second_caller(u32 live, u32 argument) reentrant;
