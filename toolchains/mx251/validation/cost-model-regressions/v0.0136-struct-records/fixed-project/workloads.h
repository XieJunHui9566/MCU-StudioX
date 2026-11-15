typedef unsigned char u8;
typedef unsigned int u16;
typedef unsigned long u32;
#ifdef __SDCC
#define ABI __reentrant __sdcccall(4)
#define XDATA __xdata
#define CODE __code
#else
#define ABI reentrant
#define XDATA xdata
#define CODE code
#endif
extern u8 XDATA inputs[64];
extern u8 XDATA output[64];
u32 identity(u32 seed,u32 count) ABI;
u32 sum_bytes(u32 seed,u32 count) ABI;
u32 copy_xor(u32 seed,u32 count) ABI;
u32 ring_buffer(u32 seed,u32 count) ABI;
u32 state_machine(u32 seed,u32 count) ABI;
u32 frame_parser(u32 seed,u32 count) ABI;
u32 struct_records(u32 seed,u32 count) ABI;
u32 crc16(u32 seed,u32 count) ABI;
u32 call_chain(u32 seed,u32 count) ABI;
u32 mix4(u32 a,u32 b,u32 c,u32 d) ABI;
