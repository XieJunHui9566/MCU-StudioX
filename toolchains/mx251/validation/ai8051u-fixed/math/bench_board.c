typedef unsigned char uint8_t;
typedef unsigned int uint16_t;
typedef unsigned long uint32_t;
#include "bench_all.h"
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
#define REPEATS 8U
#define SAMPLES 3U
static uint8_t XDATA records[18U*SAMPLES][16];
static uint16_t record_count;
static uint8_t checksum;
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
void main(void)
{
    uint8_t k,sample,n,j,hi,lo,overflow;
    uint16_t idx;
    uint32_t result,a,b;
    uint8_t *raw;
    init(); record_count=0;
    for(k=0;k<18;++k) {
        a=k>=10?0x3F900000UL:0x12345678UL;
        b=k>=10?0x3F400000UL:9UL;
        for(sample=0;sample<SAMPLES;++sample) {
            TL0=0; TH0=0; TF0=0;
            TR0=1;
            for(n=0;n<REPEATS;++n) result=invoke(k,a,b);
            TR0=0;
            hi=TH0;lo=TL0;overflow=TF0;
            idx=record_count++;
            records[idx][0]=k; records[idx][1]=0;
            records[idx][2]=sample; records[idx][3]=REPEATS;
            records[idx][4]=hi;records[idx][5]=lo;
            records[idx][6]=overflow;records[idx][7]=0;
            raw=(uint8_t *)&result;
            for(j=0;j<4;++j)records[idx][8+j]=raw[j];
            for(j=12;j<16;++j)records[idx][j]=0;
        }
    }
    for(;;)for(idx=0;idx<record_count;++idx) {
        checksum=0;send('M');send('9');
        for(j=0;j<16;++j)send(records[idx][j]);
        send(checksum);send('\r');send('\n');
    }
}
