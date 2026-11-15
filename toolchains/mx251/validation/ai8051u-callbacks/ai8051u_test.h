#ifndef AI8051U_CALLBACK_TEST_H
#define AI8051U_CALLBACK_TEST_H
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

typedef uint8_t u8;typedef unsigned short u16;typedef uint32_t u32;
__sbit __at(0xa9) ET0;
extern __xdata u8 packet[64];extern u8 transport_fault;
void transport_init(void);void transport_poll(void);u8 transport_ready(void);void transport_send(void);
#endif
