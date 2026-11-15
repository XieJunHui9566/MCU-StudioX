#ifndef APP_H
#define APP_H
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned long u32;
#ifdef __SDCC
#define PTR __xdata
#define RAM __xdata
#define ROM __code
#ifdef USE_NATIVE
#define API __reentrant __sdcccall(4)
#else
#define API __reentrant
#endif
#define LEGACY __reentrant
#elif defined(__C251__)
#define PTR far
#define RAM xdata
#define ROM code
#define API reentrant
#define LEGACY reentrant
#else
#define PTR
#define RAM
#define ROM
#define API
#define LEGACY
#endif

/* 指针传递结构，不依赖主机与目标机的结构填充或字节序相同。 */
typedef struct {u8 bytes[32];u8 head,tail,count;u16 dropped;} Ring;
typedef struct {u8 stage,length,pos,payload[16];u16 crc,wire,accepted,rejected;u32 total,digest;} Parser;
typedef struct {u32 accepted,rejected,dropped,total,digest,stage,guard,seed;} Outcome;
void ring_init(Ring PTR *r) API;
void ring_push(Ring PTR *r,u8 value) API;
u8 ring_pop(Ring PTR *r,u8 PTR *value) API;
u16 crc_step(u16 crc,u8 value) API;
void parser_init(Parser PTR *p,u32 seed) API;
void parser_feed(Parser PTR *p,u8 value) API;
void mutate_word(u32 PTR *value,u16 salt) API;
u32 revise_seed(u32 seed,u16 salt) LEGACY;
void app_run(const u8 PTR *input,u16 length,u8 chunk,u32 seed,Outcome PTR *out) API;
#endif
