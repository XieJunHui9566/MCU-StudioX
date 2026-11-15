#ifdef HOST
#include <stdio.h>
#include "app.h"
#include "fixtures.h"
_Static_assert(sizeof(u8)==1 && sizeof(u16)==2 && sizeof(u32)==4,"target widths");
int main(void){u16 i,j;u8 input[256];Outcome out;for(i=0;i<64;++i){for(j=0;j<256;++j)input[j]=j<lengths[i]?streams[i][j]:0xcc;app_run(input,lengths[i],chunks[i],seeds[i],&out);
printf("%lu %lu %lu %lu %lu %lu %lu %lu\n",out.accepted,out.rejected,out.dropped,out.total,out.digest,out.stage,out.guard,out.seed);}return 0;}
#else
typedef unsigned char uint8_t;
typedef unsigned int uint16_t;
typedef unsigned long uint32_t;
#include "app.h"
#ifdef __SDCC
__sfr __at(0x80) P0;
__sfr __at(0x93) P0M1;
__sfr __at(0x94) P0M0;
__sfr __at(0xA0) P2;
__sfr __at(0x95) P2M1;
__sfr __at(0x96) P2M0;
__sfr __at(0xB0) P3;
__sfr __at(0xB1) P3M1;
__sfr __at(0xB2) P3M0;
__sfr __at(0xA2) P_SW1;
__sfr __at(0xBA) P_SW2;
__sfr __at(0x8E) AUXR;
__sfr __at(0x98) SCON;
__sfr __at(0x99) SBUF;
__sfr __at(0xD6) T2H;
__sfr __at(0xD7) T2L;
__sfr __at(0x89) TMOD;
__sfr __at(0x8A) TL0;
__sfr __at(0x8C) TH0;
__sfr __at(0xE9) WTST;
__sfr __at(0xEA) CKCON;
__sbit __at(0xAF) EA;
__sbit __at(0x99) TI;
__sbit __at(0x8C) TR0;
__sbit __at(0x8D) TF0;
#define TM0PS (*(volatile __xdata uint8_t *)0x7EFEA0UL)
#define TFPU_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE93UL)

#define XDATA __xdata
#else
sfr P0 = 0x80;
sfr P0M1 = 0x93;
sfr P0M0 = 0x94;
sfr P2 = 0xA0;
sfr P2M1 = 0x95;
sfr P2M0 = 0x96;
sfr P3 = 0xB0;
sfr P3M1 = 0xB1;
sfr P3M0 = 0xB2;
sfr P_SW1 = 0xA2;
sfr P_SW2 = 0xBA;
sfr AUXR = 0x8E;
sfr SCON = 0x98;
sfr SBUF = 0x99;
sfr T2H = 0xD6;
sfr T2L = 0xD7;
sfr TMOD = 0x89;
sfr TL0 = 0x8A;
sfr TH0 = 0x8C;
sfr WTST = 0xE9;
sfr CKCON = 0xEA;
sbit EA = 0xAF;
sbit TI = 0x99;
sbit TR0 = 0x8C;
sbit TF0 = 0x8D;
#define TM0PS (*(volatile uint8_t far *)0x7EFEA0UL)
#define TFPU_CLKDIV (*(volatile uint8_t far *)0x7EFE93UL)

#define XDATA xdata
#endif
#include "fixtures.h"
static struct {u32 before;u8 bytes[256];u32 after;} RAM input;
static Outcome RAM outcome;
static u32 RAM results[512],timed[9];
static u8 RAM elapsed_hi[9],elapsed_lo[9];static u8 RAM overflowed[9];
static u8 checksum;
static void send(uint8_t value)
{
    uint16_t limit=65535U;
    checksum ^= value;
    SBUF=value;
    while (!TI) { if (--limit==0U) { P2=0xDBU; for (;;) {} } }
    TI=0;
}
static void init(void)
{
    EA=0;
    P0=0; P0M1=0; P0M0=0xFF;
    P2=0xFF; P2M1=0; P2M0=0xFF; P2=0x7E;
    P3|=3; P3M1&=0xFC; P3M0=(P3M0&0xFC)|2;
    P_SW1&=0x3F; AUXR&=0x67; SCON=0x40;
    T2L=0xA9; T2H=0xFF; AUXR|=0x15;
    P_SW2|=0x80; TM0PS=0; TFPU_CLKDIV=0;
    /* 手册建议 XRAM 等待 0；本次固定 40 MHz，程序等待也固定为 0。 */
    WTST=0; CKCON=0;
    TR0=0; TMOD=(TMOD&0xF0)|1;

}


static void prepare(u8 c){u16 j;input.before=0x76543210UL;input.after=0xfedcba98UL;for(j=0;j<256U;++j)input.bytes[j]=j<lengths[c]?streams[c][j]:0xcc;}
static u32 check_input(u8 c){u16 j;u32 error=(input.before^0x76543210UL)|(input.after^0xfedcba98UL);for(j=0;j<256U;++j)if(input.bytes[j]!=(j<lengths[c]?streams[c][j]:0xcc))error|=1;return error;}
static void word(u32 v){send((u8)(v>>24));send((u8)(v>>16));send((u8)(v>>8));send((u8)v);}
void main(void){u16 id;u8 c,t,k,s;u32 value;init();
for(c=0;c<64U;++c){prepare(c);app_run(input.bytes,lengths[c],chunks[c],seeds[c],&outcome);id=(u16)c*8U;
results[id]=outcome.accepted;results[id+1]=outcome.rejected;results[id+2]=outcome.dropped;results[id+3]=outcome.total;results[id+4]=outcome.digest;results[id+5]=outcome.stage;results[id+6]=outcome.guard|check_input(c);results[id+7]=outcome.seed;}
for(k=0;k<3U;++k)for(s=0;s<3U;++s){c=k==0U?0U:(k==1U?32U:63U);t=k*3U+s;prepare(c);TL0=0;TH0=0;TF0=0;TR0=1;
app_run(input.bytes,lengths[c],chunks[c],seeds[c],&outcome);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;overflowed[t]=TF0;timed[t]=outcome.digest;}
for(;;)for(id=0;id<521U;++id){checksum=0;send('M');send('9');send((u8)id);send((u8)(id>>8)*2+1);send(0);send(1);
if(id<512U){send(0);send(0);send(0);send(69);value=results[id];}else{t=(u8)(id-512U);send(elapsed_hi[t]);send(elapsed_lo[t]);send(overflowed[t]);send(70);value=timed[t];}
word(value);word(0);send(checksum);send(13);send(10);}}
#endif
