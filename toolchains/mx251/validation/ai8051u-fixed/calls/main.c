typedef unsigned char uint8_t;
typedef unsigned int uint16_t;
typedef unsigned long uint32_t;

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
#include "wide-ordinary-calls.h"
static __xdata u32 results[1280],timed[30];static __xdata u8 elapsed_hi[30],elapsed_lo[30];static __xdata u8 over[30];static u8 checksum;static volatile __data u16 irq_count;static volatile __data u8 irq_bad;__sbit __at(0xa9) ET0;
static const __code u32 inputs[128][2]={{0x00000000UL,0x00000000UL},{0x00000000UL,0xffffffffUL},{0x00000000UL,0x80008007UL},{0x00000001UL,0x00000000UL},{0x00000001UL,0xffffffffUL},{0x00000001UL,0x80008007UL},{0x0000007fUL,0x00000000UL},{0x0000007fUL,0xffffffffUL},{0x0000007fUL,0x80008007UL},{0x00000080UL,0x00000000UL},{0x00000080UL,0xffffffffUL},{0x00000080UL,0x80008007UL},{0x000000ffUL,0x00000000UL},{0x000000ffUL,0xffffffffUL},{0x000000ffUL,0x80008007UL},{0x00000100UL,0x00000000UL},{0x00000100UL,0xffffffffUL},{0x00000100UL,0x80008007UL},{0x00007fffUL,0x00000000UL},{0x00007fffUL,0xffffffffUL},{0x00007fffUL,0x80008007UL},{0x00008000UL,0x00000000UL},{0x00008000UL,0xffffffffUL},{0x00008000UL,0x80008007UL},{0x0000ffffUL,0x00000000UL},{0x0000ffffUL,0xffffffffUL},{0x0000ffffUL,0x80008007UL},{0x00010000UL,0x00000000UL},{0x00010000UL,0xffffffffUL},{0x00010000UL,0x80008007UL},{0x7f800000UL,0x00000000UL},{0x7f800000UL,0xffffffffUL},{0x7f800000UL,0x80008007UL},{0xff800000UL,0x00000000UL},{0xff800000UL,0xffffffffUL},{0xff800000UL,0x80008007UL},{0x7fc00001UL,0x00000000UL},{0x7fc00001UL,0xffffffffUL},{0x7fc00001UL,0x80008007UL},{0x7fa12345UL,0x00000000UL},{0x7fa12345UL,0xffffffffUL},{0x7fa12345UL,0x80008007UL},{0x80000000UL,0x00000000UL},{0x80000000UL,0xffffffffUL},{0x80000000UL,0x80008007UL},{0xffffffffUL,0x00000000UL},{0xffffffffUL,0xffffffffUL},{0xffffffffUL,0x80008007UL},{0x4c1706a3UL,0x16a56ec9UL},{0x18c9a656UL,0xe3f88cdaUL},{0xb8633f21UL,0x82e5a2bdUL},{0xf8534996UL,0xf55a42c8UL},{0x19861f52UL,0x6ff89a00UL},{0x08c867ebUL,0x46b4492eUL},{0x5e632edfUL,0x388f87e5UL},{0xe457e378UL,0x040119dfUL},{0xb58cf86cUL,0x32b1fa8cUL},{0x581c3018UL,0x161ee0b5UL},{0x3ccfff45UL,0x98f1c5c7UL},{0x88c94569UL,0xc80f33f1UL},{0x6238487bUL,0xdb8e49beUL},{0xd810119eUL,0xdd464b1fUL},{0x833d4563UL,0xf4319611UL},{0x83e09276UL,0xdb86f709UL},{0x0e7d30c9UL,0x79048ddaUL},{0x3553de17UL,0x756d57b5UL},{0x3bcade78UL,0xaa974cc0UL},{0x1eaae345UL,0x60932cc3UL},{0xf5d2feadUL,0x8654f20fUL},{0x948610c3UL,0x746023ccUL},{0xdcfb2626UL,0xedb02e27UL},{0x7e02cdbaUL,0xa332861fUL},{0x4940458eUL,0xc7ad0ff3UL},{0x78f5b9d1UL,0x6675cba7UL},{0xa05ce95aUL,0xe321e851UL},{0xb7212257UL,0x01ddc62cUL},{0xb648d606UL,0x44707394UL},{0xcc73161aUL,0x72ce7b30UL},{0x5da14001UL,0xc0d41393UL},{0x800ce91bUL,0x312bb05dUL},{0xbf0296d0UL,0x92251797UL},{0xc2afe882UL,0x4ccbeff7UL},{0x9ba54735UL,0xafe5f365UL},{0x6fbe81ceUL,0xec9721d6UL},{0x1a95a56aUL,0x1994733cUL},{0x2cb1781dUL,0x377015f8UL},{0xd44f9aadUL,0xcbb93661UL},{0x0af46ebaUL,0x1bb516a6UL},{0x5d25fc4eUL,0x77a088a9UL},{0x984f0d70UL,0x421ff2fdUL},{0xca271456UL,0xbd6ae0cbUL},{0xa67ec7beUL,0x6a78a1c7UL},{0xcbd7e834UL,0xa7ef8c30UL},{0xf438b2efUL,0x30c18cd4UL},{0x5d8b6669UL,0x7f1b422eUL},{0xf606fde7UL,0x36ed8383UL},{0x8ad2571fUL,0xd871f319UL},{0xf6242d18UL,0x8fb69738UL},{0x1f3ddbc9UL,0xd3f77757UL},{0xd4c0f87dUL,0xadcc7af0UL},{0xb70b6fa8UL,0xfaae80e0UL},{0x6659aca2UL,0x33bde2acUL},{0xb3bb7050UL,0x8be3e623UL},{0x77c22be5UL,0xa94e02f0UL},{0x1a601dacUL,0x48290840UL},{0x74d267abUL,0x5d986fc0UL},{0xf818c4f7UL,0xeab22fd2UL},{0x7929bd76UL,0xf6e50aeeUL},{0x9f694144UL,0xddc1080cUL},{0xda40349aUL,0x3e1c54c9UL},{0x8a73e777UL,0x7d120405UL},{0x32da49acUL,0xafc0dbd8UL},{0xc9237cc7UL,0x94dc7c54UL},{0x8b3d9cf5UL,0xce097dd0UL},{0x5b06c34aUL,0x30fb3c31UL},{0x55258a55UL,0x2cbe992aUL},{0xd444d6bcUL,0x80db3f18UL},{0xf95888c9UL,0xf6c683afUL},{0x4a19a749UL,0x80574727UL},{0x98c0ca9fUL,0xdbad38d0UL},{0xfad6649fUL,0xec544f07UL},{0x6261c684UL,0x1c88010eUL},{0x59db4e73UL,0x44695521UL},{0x3ca53332UL,0x0eb6c30aUL},{0xe12f0c06UL,0x42463527UL},{0xa6b3280fUL,0x3de2d7b2UL},{0x82950229UL,0xa111800bUL},{0xbf5acf19UL,0xbb0db746UL}};
void timer_isr(void) __interrupt(1){TR0=0;TF0=0;TH0=0xf0;TL0=0;if(c_pair(0x800001ffUL,0xfedcba07UL)!=0x97531197UL)irq_bad=1;if(c_native(0x800001ffUL,0xfedcba07UL)!=0x7db979ffUL)irq_bad=1;if(c_recur(0x800001ffUL,0xfedcba07UL)!=0xeea3cf50UL)irq_bad=1;if(c_alias(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad=1;if(c_many(0x800001ffUL,0xfedcba07UL)!=0x7e738309UL)irq_bad=1;if(c_pointer(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad=1;if(c_float(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad=1;if(c_pressure(0x800001ffUL,0xfedcba07UL)!=0xd17d7a58UL)irq_bad=1;if(c_frame(0x800001ffUL,0xfedcba07UL)!=0x6ce8ea7eUL)irq_bad=1;if(c_callback(0x800001ffUL,0xfedcba07UL)!=0x76e5d5c5UL)irq_bad=1;++irq_count;TR0=1;}
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
void main(void){u16 i,j;u8 n,*p,t,s;u32 a,b,v;init();irq_count=0;irq_bad=0;AUXR&=~0x80;TH0=0xf0;TL0=0;TF0=0;ET0=1;TR0=1;EA=1;for(i=0;i<128U;++i){a=inputs[i][0];b=inputs[i][1];j=i*10U;results[j+0]=c_pair(a,b);results[j+1]=c_native(a,b);results[j+2]=c_recur(a,b);results[j+3]=c_alias(a,b);results[j+4]=c_many(a,b);results[j+5]=c_pointer(a,b);results[j+6]=c_float(a,b);results[j+7]=c_pressure(a,b);results[j+8]=c_frame(a,b);results[j+9]=c_callback(a,b);}EA=0;ET0=0;TR0=0;for(s=0;s<3;++s){t=0+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_pair(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=3+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_native(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=6+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_recur(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=9+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_alias(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=12+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_many(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=15+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_pointer(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=18+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_float(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=21+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_pressure(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=24+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_frame(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(s=0;s<3;++s){t=27+s;a=0x12345678UL;b=0x89abcdefUL;TL0=0;TH0=0;TF0=0;TR0=1;for(j=0;j<64U;++j)v=c_callback(a,b);TR0=0;elapsed_hi[t]=TH0;elapsed_lo[t]=TL0;over[t]=TF0;timed[t]=v;}for(;;)for(i=0;i<1311U;++i){checksum=0;send('M');send('9');send((u8)i);send((u8)(i>>8)*2+1);send(0);send(1);if(i<1281U){send(0);send(0);send(0);send(71);v=i==1280U?irq_count:results[i];}else{t=(u8)(i-1281U);send(elapsed_hi[t]);send(elapsed_lo[t]);send(over[t]);send(72);v=timed[t];}p=(u8*)&v;for(n=0;n<4;++n)send(p[n]);v=i==1280U?irq_bad:0;p=(u8*)&v;for(n=0;n<4;++n)send(p[n]);send(checksum);send(13);send(10);}}