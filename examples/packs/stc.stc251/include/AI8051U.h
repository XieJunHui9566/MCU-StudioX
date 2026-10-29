/* MCU StudioX: AI8051U 32位寄存器定义。由 Generate-Ai8051UHeader.py 生成。
 * 来源、地址勘误和覆盖范围见 register-reference.json 与 README.txt。
 * 只声明寄存器，不自动初始化GPIO、时钟、外设或中断。
 * 端口名称不代表当前封装已引出对应引脚。
 * 数据表 SHA-256: b8dd481afd07afea98033a5b223c144d3d1c2c3b5d375d908b577115dee88f98
 */
#ifndef STUDIOX_AI8051U_32BIT_H
#define STUDIOX_AI8051U_32BIT_H

#include <stdint.h>

#ifndef __SDCC_mcs251
#error "AI8051U.h requires MX251/SDCC mcs251 in 32-bit source mode"
#endif

/* SFR：8位直接地址。别名保留同一地址，不占用RAM。 */
__sfr __at(0x80) P0;
__sfr __at(0x81) SP;
__sfr __at(0x82) DPL;
__sfr __at(0x83) DPH;
__sfr __at(0x84) DPXL;
__sfr __at(0x85) SPH;
__sfr __at(0x87) PCON;
__sfr __at(0x88) TCON;
__sfr __at(0x89) TMOD;
__sfr __at(0x8A) TL0;
__sfr __at(0x8B) TL1;
__sfr __at(0x8C) TH0;
__sfr __at(0x8D) TH1;
__sfr __at(0x8E) AUXR;
__sfr __at(0x8F) INTCLKO;
__sfr __at(0x90) P1;
__sfr __at(0x91) P1M1;
__sfr __at(0x92) P1M0;
__sfr __at(0x93) P0M1;
__sfr __at(0x94) P0M0;
__sfr __at(0x95) P2M1;
__sfr __at(0x96) P2M0;
__sfr __at(0x97) AUXR2;
__sfr __at(0x98) SCON;
__sfr __at(0x99) SBUF;
__sfr __at(0x9A) S2CON;
__sfr __at(0x9B) S2BUF;
__sfr __at(0x9D) IRCBAND;
__sfr __at(0x9E) LIRTRIM;
__sfr __at(0x9F) IRTRIM;
__sfr __at(0xA0) P2;
__sfr __at(0xA1) BUS_SPEED;
__sfr __at(0xA2) P_SW1;
__sfr __at(0xA6) VRTRIM;
__sfr __at(0xA8) IE;
__sfr __at(0xA9) SADDR;
__sfr __at(0xAA) WKTCL;
__sfr __at(0xAB) WKTCH;
__sfr __at(0xAC) S3CON;
__sfr __at(0xAD) S3BUF;
__sfr __at(0xAE) TA;
__sfr __at(0xAF) IE2;
__sfr __at(0xB0) P3;
__sfr __at(0xB1) P3M1;
__sfr __at(0xB2) P3M0;
__sfr __at(0xB3) P4M1;
__sfr __at(0xB4) P4M0;
__sfr __at(0xB5) IP2;
__sfr __at(0xB6) IP2H;
__sfr __at(0xB7) IPH;
__sfr __at(0xB8) IP;
__sfr __at(0xB9) SADEN;
__sfr __at(0xBA) P_SW2;
__sfr __at(0xBB) P_SW3;
__sfr __at(0xBC) ADC_CONTR;
__sfr __at(0xBD) ADC_RES;
__sfr __at(0xBE) ADC_RESL;
__sfr __at(0xBF) P_SW4;
__sfr __at(0xC0) P4;
__sfr __at(0xC1) WDT_CONTR;
__sfr __at(0xC2) IAP_DATA;
__sfr __at(0xC3) IAP_ADDRH;
__sfr __at(0xC4) IAP_ADDRL;
__sfr __at(0xC5) IAP_CMD;
__sfr __at(0xC6) IAP_TRIG;
__sfr __at(0xC7) IAP_CONTR;
__sfr __at(0xC8) P5;
__sfr __at(0xC9) P5M1;
__sfr __at(0xCA) P5M0;
__sfr __at(0xCB) P6M1;
__sfr __at(0xCC) P6M0;
__sfr __at(0xCD) SPSTAT;
__sfr __at(0xCE) SPCTL;
__sfr __at(0xCF) SPDAT;
__sfr __at(0xD0) PSW;
__sfr __at(0xD1) PSW1;
__sfr __at(0xD2) TH4;
__sfr __at(0xD3) TL4;
__sfr __at(0xD4) TH3;
__sfr __at(0xD5) TL3;
__sfr __at(0xD6) TH2;
__sfr __at(0xD7) TL2;
__sfr __at(0xD2) T4H;
__sfr __at(0xD3) T4L;
__sfr __at(0xD4) T3H;
__sfr __at(0xD5) T3L;
__sfr __at(0xD6) T2H;
__sfr __at(0xD7) T2L;
__sfr __at(0xDC) USBCLK;
__sfr __at(0xDD) T3T4M;
__sfr __at(0xDD) T4T3M;
__sfr __at(0xDE) ADCCFG;
__sfr __at(0xDF) IP3;
__sfr __at(0xE0) ACC;
__sfr __at(0xE1) P7M1;
__sfr __at(0xE2) P7M0;
__sfr __at(0xE3) DPS;
__sfr __at(0xE6) CMPCR1;
__sfr __at(0xE7) CMPCR2;
__sfr __at(0xE8) P6;
__sfr __at(0xE9) WTST;
__sfr __at(0xEA) CKCON;
__sfr __at(0xEB) MXAX;
__sfr __at(0xEC) USBDAT;
__sfr __at(0xED) DMAIR;
__sfr __at(0xEE) IP3H;
__sfr __at(0xEF) AUXINTIF;
__sfr __at(0xF0) B;
__sfr __at(0xF4) USBCON;
__sfr __at(0xF5) IAP_TPS;
__sfr __at(0xF6) IAP_ADDRE;
__sfr __at(0xF8) P7;
__sfr __at(0xFC) USBADR;
__sfr __at(0xFD) S4CON;
__sfr __at(0xFE) S4BUF;
__sfr __at(0xFF) RSTCFG;

/* 可位寻址SFR的单个位。非位寻址寄存器请使用下方掩码。 */
__sbit __at(0x80) P00;
__sbit __at(0x81) P01;
__sbit __at(0x82) P02;
__sbit __at(0x83) P03;
__sbit __at(0x84) P04;
__sbit __at(0x85) P05;
__sbit __at(0x86) P06;
__sbit __at(0x87) P07;
__sbit __at(0x8F) TF1;
__sbit __at(0x8E) TR1;
__sbit __at(0x8D) TF0;
__sbit __at(0x8C) TR0;
__sbit __at(0x8B) IE1;
__sbit __at(0x8A) IT1;
__sbit __at(0x89) IE0;
__sbit __at(0x88) IT0;
__sbit __at(0x90) P10;
__sbit __at(0x91) P11;
__sbit __at(0x92) P12;
__sbit __at(0x93) P13;
__sbit __at(0x94) P14;
__sbit __at(0x95) P15;
__sbit __at(0x96) P16;
__sbit __at(0x97) P17;
__sbit __at(0x9F) SM0;
__sbit __at(0x9E) SM1;
__sbit __at(0x9D) SM2;
__sbit __at(0x9C) REN;
__sbit __at(0x9B) TB8;
__sbit __at(0x9A) RB8;
__sbit __at(0x99) TI;
__sbit __at(0x98) RI;
__sbit __at(0xA0) P20;
__sbit __at(0xA1) P21;
__sbit __at(0xA2) P22;
__sbit __at(0xA3) P23;
__sbit __at(0xA4) P24;
__sbit __at(0xA5) P25;
__sbit __at(0xA6) P26;
__sbit __at(0xA7) P27;
__sbit __at(0xAF) EA;
__sbit __at(0xAE) ELVD;
__sbit __at(0xAD) EADC;
__sbit __at(0xAC) ES;
__sbit __at(0xAB) ET1;
__sbit __at(0xAA) EX1;
__sbit __at(0xA9) ET0;
__sbit __at(0xA8) EX0;
__sbit __at(0xB0) P30;
__sbit __at(0xB1) P31;
__sbit __at(0xB2) P32;
__sbit __at(0xB3) P33;
__sbit __at(0xB4) P34;
__sbit __at(0xB5) P35;
__sbit __at(0xB6) P36;
__sbit __at(0xB7) P37;
__sbit __at(0xB7) RD;
__sbit __at(0xB6) WR;
__sbit __at(0xB5) T1;
__sbit __at(0xB4) T0;
__sbit __at(0xB3) INT1;
__sbit __at(0xB2) INT0;
__sbit __at(0xB1) TXD;
__sbit __at(0xB0) RXD;
__sbit __at(0xBF) PPCA;
__sbit __at(0xBE) PLVD;
__sbit __at(0xBD) PADC;
__sbit __at(0xBC) PS;
__sbit __at(0xBB) PT1;
__sbit __at(0xBA) PX1;
__sbit __at(0xB9) PT0;
__sbit __at(0xB8) PX0;
__sbit __at(0xC0) P40;
__sbit __at(0xC1) P41;
__sbit __at(0xC2) P42;
__sbit __at(0xC3) P43;
__sbit __at(0xC4) P44;
__sbit __at(0xC5) P45;
__sbit __at(0xC6) P46;
__sbit __at(0xC7) P47;
__sbit __at(0xC8) P50;
__sbit __at(0xC9) P51;
__sbit __at(0xCA) P52;
__sbit __at(0xCB) P53;
__sbit __at(0xCC) P54;
__sbit __at(0xCD) P55;
__sbit __at(0xCE) P56;
__sbit __at(0xCF) P57;
__sbit __at(0xD7) CY;
__sbit __at(0xD6) AC;
__sbit __at(0xD5) F0;
__sbit __at(0xD4) RS1;
__sbit __at(0xD3) RS0;
__sbit __at(0xD2) OV;
__sbit __at(0xD1) F1;
__sbit __at(0xD0) P;
__sbit __at(0xE8) P60;
__sbit __at(0xE9) P61;
__sbit __at(0xEA) P62;
__sbit __at(0xEB) P63;
__sbit __at(0xEC) P64;
__sbit __at(0xED) P65;
__sbit __at(0xEE) P66;
__sbit __at(0xEF) P67;
__sbit __at(0xF8) P70;
__sbit __at(0xF9) P71;
__sbit __at(0xFA) P72;
__sbit __at(0xFB) P73;
__sbit __at(0xFC) P74;
__sbit __at(0xFD) P75;
__sbit __at(0xFE) P76;
__sbit __at(0xFF) P77;

/* 扩展寄存器：必须先 P_SW2 |= EAXFR；使用完整24位地址和8位volatile访问。
 * 高低字节有硬件访问顺序，不能随意合并为uint16_t/uint32_t指针。 */

/* XFR 0x7EFE00 */
#define CLKSEL (*(volatile __xdata uint8_t *)0x7EFE00UL)
#define CLKDIV (*(volatile __xdata uint8_t *)0x7EFE01UL)
#define HIRCCR (*(volatile __xdata uint8_t *)0x7EFE02UL)
#define XOSCCR (*(volatile __xdata uint8_t *)0x7EFE03UL)
#define IRC32KCR (*(volatile __xdata uint8_t *)0x7EFE04UL)
#define MCLKOCR (*(volatile __xdata uint8_t *)0x7EFE05UL)
#define IRCDB (*(volatile __xdata uint8_t *)0x7EFE06UL)
#define IRC48MCR (*(volatile __xdata uint8_t *)0x7EFE07UL)
#define X32KCR (*(volatile __xdata uint8_t *)0x7EFE08UL)
#define HSCLKDIV (*(volatile __xdata uint8_t *)0x7EFE0BUL)
#define P0PU (*(volatile __xdata uint8_t *)0x7EFE10UL)
#define P1PU (*(volatile __xdata uint8_t *)0x7EFE11UL)
#define P2PU (*(volatile __xdata uint8_t *)0x7EFE12UL)
#define P3PU (*(volatile __xdata uint8_t *)0x7EFE13UL)
#define P4PU (*(volatile __xdata uint8_t *)0x7EFE14UL)
#define P5PU (*(volatile __xdata uint8_t *)0x7EFE15UL)
#define P6PU (*(volatile __xdata uint8_t *)0x7EFE16UL)
#define P7PU (*(volatile __xdata uint8_t *)0x7EFE17UL)
#define P0NCS (*(volatile __xdata uint8_t *)0x7EFE18UL)
#define P1NCS (*(volatile __xdata uint8_t *)0x7EFE19UL)
#define P2NCS (*(volatile __xdata uint8_t *)0x7EFE1AUL)
#define P3NCS (*(volatile __xdata uint8_t *)0x7EFE1BUL)
#define P4NCS (*(volatile __xdata uint8_t *)0x7EFE1CUL)
#define P5NCS (*(volatile __xdata uint8_t *)0x7EFE1DUL)
#define P6NCS (*(volatile __xdata uint8_t *)0x7EFE1EUL)
#define P7NCS (*(volatile __xdata uint8_t *)0x7EFE1FUL)
#define P0SR (*(volatile __xdata uint8_t *)0x7EFE20UL)
#define P1SR (*(volatile __xdata uint8_t *)0x7EFE21UL)
#define P2SR (*(volatile __xdata uint8_t *)0x7EFE22UL)
#define P3SR (*(volatile __xdata uint8_t *)0x7EFE23UL)
#define P4SR (*(volatile __xdata uint8_t *)0x7EFE24UL)
#define P5SR (*(volatile __xdata uint8_t *)0x7EFE25UL)
#define P6SR (*(volatile __xdata uint8_t *)0x7EFE26UL)
#define P7SR (*(volatile __xdata uint8_t *)0x7EFE27UL)
#define P0DR (*(volatile __xdata uint8_t *)0x7EFE28UL)
#define P1DR (*(volatile __xdata uint8_t *)0x7EFE29UL)
#define P2DR (*(volatile __xdata uint8_t *)0x7EFE2AUL)
#define P3DR (*(volatile __xdata uint8_t *)0x7EFE2BUL)
#define P4DR (*(volatile __xdata uint8_t *)0x7EFE2CUL)
#define P5DR (*(volatile __xdata uint8_t *)0x7EFE2DUL)
#define P6DR (*(volatile __xdata uint8_t *)0x7EFE2EUL)
#define P7DR (*(volatile __xdata uint8_t *)0x7EFE2FUL)
#define P0IE (*(volatile __xdata uint8_t *)0x7EFE30UL)
#define P1IE (*(volatile __xdata uint8_t *)0x7EFE31UL)
#define P2IE (*(volatile __xdata uint8_t *)0x7EFE32UL)
#define P3IE (*(volatile __xdata uint8_t *)0x7EFE33UL)
#define P4IE (*(volatile __xdata uint8_t *)0x7EFE34UL)
#define P5IE (*(volatile __xdata uint8_t *)0x7EFE35UL)
#define P6IE (*(volatile __xdata uint8_t *)0x7EFE36UL)
#define P7IE (*(volatile __xdata uint8_t *)0x7EFE37UL)
#define P0PD (*(volatile __xdata uint8_t *)0x7EFE40UL)
#define P1PD (*(volatile __xdata uint8_t *)0x7EFE41UL)
#define P2PD (*(volatile __xdata uint8_t *)0x7EFE42UL)
#define P3PD (*(volatile __xdata uint8_t *)0x7EFE43UL)
#define P4PD (*(volatile __xdata uint8_t *)0x7EFE44UL)
#define P5PD (*(volatile __xdata uint8_t *)0x7EFE45UL)
#define P6PD (*(volatile __xdata uint8_t *)0x7EFE46UL)
#define P7PD (*(volatile __xdata uint8_t *)0x7EFE47UL)
#define P0BP (*(volatile __xdata uint8_t *)0x7EFE48UL)
#define P1BP (*(volatile __xdata uint8_t *)0x7EFE49UL)
#define P2BP (*(volatile __xdata uint8_t *)0x7EFE4AUL)
#define P3BP (*(volatile __xdata uint8_t *)0x7EFE4BUL)
#define P4BP (*(volatile __xdata uint8_t *)0x7EFE4CUL)
#define P5BP (*(volatile __xdata uint8_t *)0x7EFE4DUL)
#define P6BP (*(volatile __xdata uint8_t *)0x7EFE4EUL)
#define P7BP (*(volatile __xdata uint8_t *)0x7EFE4FUL)
#define LCMIFCFG (*(volatile __xdata uint8_t *)0x7EFE50UL)
#define LCMIFCFG2 (*(volatile __xdata uint8_t *)0x7EFE51UL)
#define LCMIFCR (*(volatile __xdata uint8_t *)0x7EFE52UL)
#define LCMIFSTA (*(volatile __xdata uint8_t *)0x7EFE53UL)
#define LCMIFDATL (*(volatile __xdata uint8_t *)0x7EFE54UL)
#define LCMIFDATH (*(volatile __xdata uint8_t *)0x7EFE55UL)
#define LCMIFPSCR (*(volatile __xdata uint8_t *)0x7EFE56UL)
#define RTCCR (*(volatile __xdata uint8_t *)0x7EFE60UL)
#define RTCCFG (*(volatile __xdata uint8_t *)0x7EFE61UL)
#define RTCIEN (*(volatile __xdata uint8_t *)0x7EFE62UL)
#define RTCIF (*(volatile __xdata uint8_t *)0x7EFE63UL)
#define ALAHOUR (*(volatile __xdata uint8_t *)0x7EFE64UL)
#define ALAMIN (*(volatile __xdata uint8_t *)0x7EFE65UL)
#define ALASEC (*(volatile __xdata uint8_t *)0x7EFE66UL)
#define ALASSEC (*(volatile __xdata uint8_t *)0x7EFE67UL)
#define INIYEAR (*(volatile __xdata uint8_t *)0x7EFE68UL)
#define INIMONTH (*(volatile __xdata uint8_t *)0x7EFE69UL)
#define INIDAY (*(volatile __xdata uint8_t *)0x7EFE6AUL)
#define INIHOUR (*(volatile __xdata uint8_t *)0x7EFE6BUL)
#define INIMIN (*(volatile __xdata uint8_t *)0x7EFE6CUL)
#define INISEC (*(volatile __xdata uint8_t *)0x7EFE6DUL)
#define INISSEC (*(volatile __xdata uint8_t *)0x7EFE6EUL)
#define INIWEEK (*(volatile __xdata uint8_t *)0x7EFE6FUL)
#define WEEK (*(volatile __xdata uint8_t *)0x7EFE6FUL)
#define YEAR (*(volatile __xdata uint8_t *)0x7EFE70UL)
#define MONTH (*(volatile __xdata uint8_t *)0x7EFE71UL)
#define DAY (*(volatile __xdata uint8_t *)0x7EFE72UL)
#define HOUR (*(volatile __xdata uint8_t *)0x7EFE73UL)
#define MIN (*(volatile __xdata uint8_t *)0x7EFE74UL)
#define SEC (*(volatile __xdata uint8_t *)0x7EFE75UL)
#define SSEC (*(volatile __xdata uint8_t *)0x7EFE76UL)
#define T11CR (*(volatile __xdata uint8_t *)0x7EFE78UL)
#define T11PS (*(volatile __xdata uint8_t *)0x7EFE79UL)
#define T11H (*(volatile __xdata uint8_t *)0x7EFE7AUL)
#define T11L (*(volatile __xdata uint8_t *)0x7EFE7BUL)
#define I2CCFG (*(volatile __xdata uint8_t *)0x7EFE80UL)
#define I2CMSCR (*(volatile __xdata uint8_t *)0x7EFE81UL)
#define I2CMSST (*(volatile __xdata uint8_t *)0x7EFE82UL)
#define I2CSLCR (*(volatile __xdata uint8_t *)0x7EFE83UL)
#define I2CSLST (*(volatile __xdata uint8_t *)0x7EFE84UL)
#define I2CSLADR (*(volatile __xdata uint8_t *)0x7EFE85UL)
#define I2CTXD (*(volatile __xdata uint8_t *)0x7EFE86UL)
#define I2CRXD (*(volatile __xdata uint8_t *)0x7EFE87UL)
#define I2CMSAUX (*(volatile __xdata uint8_t *)0x7EFE88UL)
#define I2CPSCR (*(volatile __xdata uint8_t *)0x7EFE89UL)
#define SPI_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE90UL)
#define PWMA_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE91UL)
#define PWMB_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE92UL)
#define TFPU_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE93UL)
#define I2S_CLKDIV (*(volatile __xdata uint8_t *)0x7EFE94UL)
#define RSTFLAG (*(volatile __xdata uint8_t *)0x7EFE99UL)
#define RSTCR0 (*(volatile __xdata uint8_t *)0x7EFE9AUL)
#define RSTCR1 (*(volatile __xdata uint8_t *)0x7EFE9BUL)
#define RSTCR2 (*(volatile __xdata uint8_t *)0x7EFE9CUL)
#define RSTCR3 (*(volatile __xdata uint8_t *)0x7EFE9DUL)
#define RSTCR4 (*(volatile __xdata uint8_t *)0x7EFE9EUL)
#define RSTCR5 (*(volatile __xdata uint8_t *)0x7EFE9FUL)
#define TM0PS (*(volatile __xdata uint8_t *)0x7EFEA0UL)
#define TM1PS (*(volatile __xdata uint8_t *)0x7EFEA1UL)
#define TM2PS (*(volatile __xdata uint8_t *)0x7EFEA2UL)
#define TM3PS (*(volatile __xdata uint8_t *)0x7EFEA3UL)
#define TM4PS (*(volatile __xdata uint8_t *)0x7EFEA4UL)
#define ADCTIM (*(volatile __xdata uint8_t *)0x7EFEA8UL)
#define ADCEXCFG (*(volatile __xdata uint8_t *)0x7EFEADUL)
#define CMPEXCFG (*(volatile __xdata uint8_t *)0x7EFEAEUL)
#define PWMA_ETRPS (*(volatile __xdata uint8_t *)0x7EFEB0UL)
#define PWMA_ENO (*(volatile __xdata uint8_t *)0x7EFEB1UL)
#define PWMA_PS (*(volatile __xdata uint8_t *)0x7EFEB2UL)
#define PWMA_IOAUX (*(volatile __xdata uint8_t *)0x7EFEB3UL)
#define PWMB_ETRPS (*(volatile __xdata uint8_t *)0x7EFEB4UL)
#define PWMB_ENO (*(volatile __xdata uint8_t *)0x7EFEB5UL)
#define PWMB_PS (*(volatile __xdata uint8_t *)0x7EFEB6UL)
#define PWMB_IOAUX (*(volatile __xdata uint8_t *)0x7EFEB7UL)
#define PWMA_PS2 (*(volatile __xdata uint8_t *)0x7EFEB8UL)
#define PWMA_RCRH (*(volatile __xdata uint8_t *)0x7EFEB9UL)
#define PWMB_RCRH (*(volatile __xdata uint8_t *)0x7EFEBAUL)
#define PWMA_CR1 (*(volatile __xdata uint8_t *)0x7EFEC0UL)
#define PWMA_CR2 (*(volatile __xdata uint8_t *)0x7EFEC1UL)
#define PWMA_SMCR (*(volatile __xdata uint8_t *)0x7EFEC2UL)
#define PWMA_ETR (*(volatile __xdata uint8_t *)0x7EFEC3UL)
#define PWMA_IER (*(volatile __xdata uint8_t *)0x7EFEC4UL)
#define PWMA_SR1 (*(volatile __xdata uint8_t *)0x7EFEC5UL)
#define PWMA_SR2 (*(volatile __xdata uint8_t *)0x7EFEC6UL)
#define PWMA_EGR (*(volatile __xdata uint8_t *)0x7EFEC7UL)
#define PWMA_CCMR1 (*(volatile __xdata uint8_t *)0x7EFEC8UL)
#define PWMA_CCMR2 (*(volatile __xdata uint8_t *)0x7EFEC9UL)
#define PWMA_CCMR3 (*(volatile __xdata uint8_t *)0x7EFECAUL)
#define PWMA_CCMR4 (*(volatile __xdata uint8_t *)0x7EFECBUL)
#define PWMA_CCER1 (*(volatile __xdata uint8_t *)0x7EFECCUL)
#define PWMA_CCER2 (*(volatile __xdata uint8_t *)0x7EFECDUL)
#define PWMA_CNTRH (*(volatile __xdata uint8_t *)0x7EFECEUL)
#define PWMA_CNTRL (*(volatile __xdata uint8_t *)0x7EFECFUL)
#define PWMA_PSCRH (*(volatile __xdata uint8_t *)0x7EFED0UL)
#define PWMA_PSCRL (*(volatile __xdata uint8_t *)0x7EFED1UL)
#define PWMA_ARRH (*(volatile __xdata uint8_t *)0x7EFED2UL)
#define PWMA_ARRL (*(volatile __xdata uint8_t *)0x7EFED3UL)
#define PWMA_RCR (*(volatile __xdata uint8_t *)0x7EFED4UL)
#define PWMA_CCR1H (*(volatile __xdata uint8_t *)0x7EFED5UL)
#define PWMA_CCR1L (*(volatile __xdata uint8_t *)0x7EFED6UL)
#define PWMA_CCR2H (*(volatile __xdata uint8_t *)0x7EFED7UL)
#define PWMA_CCR2L (*(volatile __xdata uint8_t *)0x7EFED8UL)
#define PWMA_CCR3H (*(volatile __xdata uint8_t *)0x7EFED9UL)
#define PWMA_CCR3L (*(volatile __xdata uint8_t *)0x7EFEDAUL)
#define PWMA_CCR4H (*(volatile __xdata uint8_t *)0x7EFEDBUL)
#define PWMA_CCR4L (*(volatile __xdata uint8_t *)0x7EFEDCUL)
#define PWMA_BKR (*(volatile __xdata uint8_t *)0x7EFEDDUL)
#define PWMA_DTR (*(volatile __xdata uint8_t *)0x7EFEDEUL)
#define PWMA_OISR (*(volatile __xdata uint8_t *)0x7EFEDFUL)
#define PWMB_CR1 (*(volatile __xdata uint8_t *)0x7EFEE0UL)
#define PWMB_CR2 (*(volatile __xdata uint8_t *)0x7EFEE1UL)
#define PWMB_SMCR (*(volatile __xdata uint8_t *)0x7EFEE2UL)
#define PWMB_ETR (*(volatile __xdata uint8_t *)0x7EFEE3UL)
#define PWMB_IER (*(volatile __xdata uint8_t *)0x7EFEE4UL)
#define PWMB_SR1 (*(volatile __xdata uint8_t *)0x7EFEE5UL)
#define PWMB_SR2 (*(volatile __xdata uint8_t *)0x7EFEE6UL)
#define PWMB_EGR (*(volatile __xdata uint8_t *)0x7EFEE7UL)
#define PWMB_CCMR1 (*(volatile __xdata uint8_t *)0x7EFEE8UL)
#define PWMB_CCMR2 (*(volatile __xdata uint8_t *)0x7EFEE9UL)
#define PWMB_CCMR3 (*(volatile __xdata uint8_t *)0x7EFEEAUL)
#define PWMB_CCMR4 (*(volatile __xdata uint8_t *)0x7EFEEBUL)
#define PWMB_CCER1 (*(volatile __xdata uint8_t *)0x7EFEECUL)
#define PWMB_CCER2 (*(volatile __xdata uint8_t *)0x7EFEEDUL)
#define PWMB_CNTRH (*(volatile __xdata uint8_t *)0x7EFEEEUL)
#define PWMB_CNTRL (*(volatile __xdata uint8_t *)0x7EFEEFUL)
#define PWMB_PSCRH (*(volatile __xdata uint8_t *)0x7EFEF0UL)
#define PWMB_PSCRL (*(volatile __xdata uint8_t *)0x7EFEF1UL)
#define PWMB_ARRH (*(volatile __xdata uint8_t *)0x7EFEF2UL)
#define PWMB_ARRL (*(volatile __xdata uint8_t *)0x7EFEF3UL)
#define PWMB_RCR (*(volatile __xdata uint8_t *)0x7EFEF4UL)
#define PWMB_CCR5H (*(volatile __xdata uint8_t *)0x7EFEF5UL)
#define PWMB_CCR5L (*(volatile __xdata uint8_t *)0x7EFEF6UL)
#define PWMB_CCR6H (*(volatile __xdata uint8_t *)0x7EFEF7UL)
#define PWMB_CCR6L (*(volatile __xdata uint8_t *)0x7EFEF8UL)
#define PWMB_CCR7H (*(volatile __xdata uint8_t *)0x7EFEF9UL)
#define PWMB_CCR7L (*(volatile __xdata uint8_t *)0x7EFEFAUL)
#define PWMB_CCR8H (*(volatile __xdata uint8_t *)0x7EFEFBUL)
#define PWMB_CCR8L (*(volatile __xdata uint8_t *)0x7EFEFCUL)
#define PWMB_BKR (*(volatile __xdata uint8_t *)0x7EFEFDUL)
#define PWMB_DTR (*(volatile __xdata uint8_t *)0x7EFEFEUL)
#define PWMB_OISR (*(volatile __xdata uint8_t *)0x7EFEFFUL)

/* XFR 0x7EFD00 */
#define P0INTE (*(volatile __xdata uint8_t *)0x7EFD00UL)
#define P1INTE (*(volatile __xdata uint8_t *)0x7EFD01UL)
#define P2INTE (*(volatile __xdata uint8_t *)0x7EFD02UL)
#define P3INTE (*(volatile __xdata uint8_t *)0x7EFD03UL)
#define P4INTE (*(volatile __xdata uint8_t *)0x7EFD04UL)
#define P5INTE (*(volatile __xdata uint8_t *)0x7EFD05UL)
#define P6INTE (*(volatile __xdata uint8_t *)0x7EFD06UL)
#define P7INTE (*(volatile __xdata uint8_t *)0x7EFD07UL)
#define P0INTF (*(volatile __xdata uint8_t *)0x7EFD10UL)
#define P1INTF (*(volatile __xdata uint8_t *)0x7EFD11UL)
#define P2INTF (*(volatile __xdata uint8_t *)0x7EFD12UL)
#define P3INTF (*(volatile __xdata uint8_t *)0x7EFD13UL)
#define P4INTF (*(volatile __xdata uint8_t *)0x7EFD14UL)
#define P5INTF (*(volatile __xdata uint8_t *)0x7EFD15UL)
#define P6INTF (*(volatile __xdata uint8_t *)0x7EFD16UL)
#define P7INTF (*(volatile __xdata uint8_t *)0x7EFD17UL)
#define P0IM0 (*(volatile __xdata uint8_t *)0x7EFD20UL)
#define P1IM0 (*(volatile __xdata uint8_t *)0x7EFD21UL)
#define P2IM0 (*(volatile __xdata uint8_t *)0x7EFD22UL)
#define P3IM0 (*(volatile __xdata uint8_t *)0x7EFD23UL)
#define P4IM0 (*(volatile __xdata uint8_t *)0x7EFD24UL)
#define P5IM0 (*(volatile __xdata uint8_t *)0x7EFD25UL)
#define P6IM0 (*(volatile __xdata uint8_t *)0x7EFD26UL)
#define P7IM0 (*(volatile __xdata uint8_t *)0x7EFD27UL)
#define P0IM1 (*(volatile __xdata uint8_t *)0x7EFD30UL)
#define P1IM1 (*(volatile __xdata uint8_t *)0x7EFD31UL)
#define P2IM1 (*(volatile __xdata uint8_t *)0x7EFD32UL)
#define P3IM1 (*(volatile __xdata uint8_t *)0x7EFD33UL)
#define P4IM1 (*(volatile __xdata uint8_t *)0x7EFD34UL)
#define P5IM1 (*(volatile __xdata uint8_t *)0x7EFD35UL)
#define P6IM1 (*(volatile __xdata uint8_t *)0x7EFD36UL)
#define P7IM1 (*(volatile __xdata uint8_t *)0x7EFD37UL)
#define P0WKUE (*(volatile __xdata uint8_t *)0x7EFD40UL)
#define P1WKUE (*(volatile __xdata uint8_t *)0x7EFD41UL)
#define P2WKUE (*(volatile __xdata uint8_t *)0x7EFD42UL)
#define P3WKUE (*(volatile __xdata uint8_t *)0x7EFD43UL)
#define P4WKUE (*(volatile __xdata uint8_t *)0x7EFD44UL)
#define P5WKUE (*(volatile __xdata uint8_t *)0x7EFD45UL)
#define P6WKUE (*(volatile __xdata uint8_t *)0x7EFD46UL)
#define P7WKUE (*(volatile __xdata uint8_t *)0x7EFD47UL)
#define CCAPM2 (*(volatile __xdata uint8_t *)0x7EFD50UL)
#define CCAP2L (*(volatile __xdata uint8_t *)0x7EFD51UL)
#define CCAP2H (*(volatile __xdata uint8_t *)0x7EFD52UL)
#define PCA_PWM2 (*(volatile __xdata uint8_t *)0x7EFD53UL)
#define CCAPM3 (*(volatile __xdata uint8_t *)0x7EFD54UL)
#define CCAP3L (*(volatile __xdata uint8_t *)0x7EFD55UL)
#define CCAP3H (*(volatile __xdata uint8_t *)0x7EFD56UL)
#define PCA_PWM3 (*(volatile __xdata uint8_t *)0x7EFD57UL)
#define CCAPM0 (*(volatile __xdata uint8_t *)0x7EFD58UL)
#define CCAP0L (*(volatile __xdata uint8_t *)0x7EFD59UL)
#define CCAP0H (*(volatile __xdata uint8_t *)0x7EFD5AUL)
#define PCA_PWM0 (*(volatile __xdata uint8_t *)0x7EFD5BUL)
#define CCAPM1 (*(volatile __xdata uint8_t *)0x7EFD5CUL)
#define CCAP1L (*(volatile __xdata uint8_t *)0x7EFD5DUL)
#define CCAP1H (*(volatile __xdata uint8_t *)0x7EFD5EUL)
#define PCA_PWM1 (*(volatile __xdata uint8_t *)0x7EFD5FUL)
#define PINIPL (*(volatile __xdata uint8_t *)0x7EFD60UL)
#define PINIPH (*(volatile __xdata uint8_t *)0x7EFD61UL)
#define CCON (*(volatile __xdata uint8_t *)0x7EFD64UL)
#define CL (*(volatile __xdata uint8_t *)0x7EFD65UL)
#define CH (*(volatile __xdata uint8_t *)0x7EFD66UL)
#define CMOD (*(volatile __xdata uint8_t *)0x7EFD67UL)
#define UR1TOCR (*(volatile __xdata uint8_t *)0x7EFD70UL)
#define UR1TOSR (*(volatile __xdata uint8_t *)0x7EFD71UL)
#define UR1TOTH (*(volatile __xdata uint8_t *)0x7EFD72UL)
#define UR1TOTL (*(volatile __xdata uint8_t *)0x7EFD73UL)
#define UR2TOCR (*(volatile __xdata uint8_t *)0x7EFD74UL)
#define UR2TOSR (*(volatile __xdata uint8_t *)0x7EFD75UL)
#define UR2TOTH (*(volatile __xdata uint8_t *)0x7EFD76UL)
#define UR2TOTL (*(volatile __xdata uint8_t *)0x7EFD77UL)
#define UR3TOCR (*(volatile __xdata uint8_t *)0x7EFD78UL)
#define UR3TOSR (*(volatile __xdata uint8_t *)0x7EFD79UL)
#define UR3TOTH (*(volatile __xdata uint8_t *)0x7EFD7AUL)
#define UR3TOTL (*(volatile __xdata uint8_t *)0x7EFD7BUL)
#define UR4TOCR (*(volatile __xdata uint8_t *)0x7EFD7CUL)
#define UR4TOSR (*(volatile __xdata uint8_t *)0x7EFD7DUL)
#define UR4TOTH (*(volatile __xdata uint8_t *)0x7EFD7EUL)
#define UR4TOTL (*(volatile __xdata uint8_t *)0x7EFD7FUL)
#define SPITOCR (*(volatile __xdata uint8_t *)0x7EFD80UL)
#define SPITOSR (*(volatile __xdata uint8_t *)0x7EFD81UL)
#define SPITOTH (*(volatile __xdata uint8_t *)0x7EFD82UL)
#define SPITOTL (*(volatile __xdata uint8_t *)0x7EFD83UL)
#define I2CTOCR (*(volatile __xdata uint8_t *)0x7EFD84UL)
#define I2CTOSR (*(volatile __xdata uint8_t *)0x7EFD85UL)
#define I2CTOTH (*(volatile __xdata uint8_t *)0x7EFD86UL)
#define I2CTOTL (*(volatile __xdata uint8_t *)0x7EFD87UL)
#define UR1TOTE (*(volatile __xdata uint8_t *)0x7EFD88UL)
#define UR2TOTE (*(volatile __xdata uint8_t *)0x7EFD89UL)
#define UR3TOTE (*(volatile __xdata uint8_t *)0x7EFD8AUL)
#define UR4TOTE (*(volatile __xdata uint8_t *)0x7EFD8BUL)
#define SPITOTE (*(volatile __xdata uint8_t *)0x7EFD8CUL)
#define I2CTOTE (*(volatile __xdata uint8_t *)0x7EFD8DUL)
#define I2SCR (*(volatile __xdata uint8_t *)0x7EFD98UL)
#define I2SSR (*(volatile __xdata uint8_t *)0x7EFD99UL)
#define I2SDRH (*(volatile __xdata uint8_t *)0x7EFD9AUL)
#define I2SDRL (*(volatile __xdata uint8_t *)0x7EFD9BUL)
#define I2SPRH (*(volatile __xdata uint8_t *)0x7EFD9CUL)
#define I2SPRL (*(volatile __xdata uint8_t *)0x7EFD9DUL)
#define I2SCFGH (*(volatile __xdata uint8_t *)0x7EFD9EUL)
#define I2SCFGL (*(volatile __xdata uint8_t *)0x7EFD9FUL)
#define I2SMD (*(volatile __xdata uint8_t *)0x7EFDA0UL)
#define I2SMCKDIV (*(volatile __xdata uint8_t *)0x7EFDA1UL)
#define CRECR (*(volatile __xdata uint8_t *)0x7EFDA8UL)
#define CRECNTH (*(volatile __xdata uint8_t *)0x7EFDA9UL)
#define CRECNTL (*(volatile __xdata uint8_t *)0x7EFDAAUL)
#define CRERES (*(volatile __xdata uint8_t *)0x7EFDABUL)
#define S2CFG (*(volatile __xdata uint8_t *)0x7EFDB4UL)
#define S2ADDR (*(volatile __xdata uint8_t *)0x7EFDB5UL)
#define S2ADEN (*(volatile __xdata uint8_t *)0x7EFDB6UL)
#define USARTCR1 (*(volatile __xdata uint8_t *)0x7EFDC0UL)
#define USARTCR2 (*(volatile __xdata uint8_t *)0x7EFDC1UL)
#define USARTCR3 (*(volatile __xdata uint8_t *)0x7EFDC2UL)
#define USARTCR4 (*(volatile __xdata uint8_t *)0x7EFDC3UL)
#define USARTCR5 (*(volatile __xdata uint8_t *)0x7EFDC4UL)
#define USARTGTR (*(volatile __xdata uint8_t *)0x7EFDC5UL)
#define USARTBRH (*(volatile __xdata uint8_t *)0x7EFDC6UL)
#define USARTBRL (*(volatile __xdata uint8_t *)0x7EFDC7UL)
#define USART2CR1 (*(volatile __xdata uint8_t *)0x7EFDC8UL)
#define USART2CR2 (*(volatile __xdata uint8_t *)0x7EFDC9UL)
#define USART2CR3 (*(volatile __xdata uint8_t *)0x7EFDCAUL)
#define USART2CR4 (*(volatile __xdata uint8_t *)0x7EFDCBUL)
#define USART2CR5 (*(volatile __xdata uint8_t *)0x7EFDCCUL)
#define USART2GTR (*(volatile __xdata uint8_t *)0x7EFDCDUL)
#define USART2BRH (*(volatile __xdata uint8_t *)0x7EFDCEUL)
#define USART2BRL (*(volatile __xdata uint8_t *)0x7EFDCFUL)
#define CHIPID0 (*(const volatile __xdata uint8_t *)0x7EFDE0UL)
#define CHIPID1 (*(const volatile __xdata uint8_t *)0x7EFDE1UL)
#define CHIPID2 (*(const volatile __xdata uint8_t *)0x7EFDE2UL)
#define CHIPID3 (*(const volatile __xdata uint8_t *)0x7EFDE3UL)
#define CHIPID4 (*(const volatile __xdata uint8_t *)0x7EFDE4UL)
#define CHIPID5 (*(const volatile __xdata uint8_t *)0x7EFDE5UL)
#define CHIPID6 (*(const volatile __xdata uint8_t *)0x7EFDE6UL)
#define CHIPID7 (*(const volatile __xdata uint8_t *)0x7EFDE7UL)
#define CHIPID8 (*(const volatile __xdata uint8_t *)0x7EFDE8UL)
#define CHIPID9 (*(const volatile __xdata uint8_t *)0x7EFDE9UL)
#define CHIPID10 (*(const volatile __xdata uint8_t *)0x7EFDEAUL)
#define CHIPID11 (*(const volatile __xdata uint8_t *)0x7EFDEBUL)
#define CHIPID12 (*(const volatile __xdata uint8_t *)0x7EFDECUL)
#define CHIPID13 (*(const volatile __xdata uint8_t *)0x7EFDEDUL)
#define CHIPID14 (*(const volatile __xdata uint8_t *)0x7EFDEEUL)
#define CHIPID15 (*(const volatile __xdata uint8_t *)0x7EFDEFUL)
#define CHIPID16 (*(const volatile __xdata uint8_t *)0x7EFDF0UL)
#define CHIPID17 (*(const volatile __xdata uint8_t *)0x7EFDF1UL)
#define CHIPID18 (*(const volatile __xdata uint8_t *)0x7EFDF2UL)
#define CHIPID19 (*(const volatile __xdata uint8_t *)0x7EFDF3UL)
#define CHIPID20 (*(const volatile __xdata uint8_t *)0x7EFDF4UL)
#define CHIPID21 (*(const volatile __xdata uint8_t *)0x7EFDF5UL)
#define CHIPID22 (*(const volatile __xdata uint8_t *)0x7EFDF6UL)
#define CHIPID23 (*(const volatile __xdata uint8_t *)0x7EFDF7UL)
#define CHIPID24 (*(const volatile __xdata uint8_t *)0x7EFDF8UL)
#define CHIPID25 (*(const volatile __xdata uint8_t *)0x7EFDF9UL)
#define CHIPID26 (*(const volatile __xdata uint8_t *)0x7EFDFAUL)
#define CHIPID27 (*(const volatile __xdata uint8_t *)0x7EFDFBUL)
#define CHIPID28 (*(const volatile __xdata uint8_t *)0x7EFDFCUL)
#define CHIPID29 (*(const volatile __xdata uint8_t *)0x7EFDFDUL)
#define CHIPID30 (*(const volatile __xdata uint8_t *)0x7EFDFEUL)
#define CHIPID31 (*(const volatile __xdata uint8_t *)0x7EFDFFUL)

/* XFR 0x7EFB00 */
#define CHIPIDX0 (*(const volatile __xdata uint8_t *)0x7EFBD0UL)
#define CHIPIDX1 (*(const volatile __xdata uint8_t *)0x7EFBD1UL)
#define CHIPIDX2 (*(const volatile __xdata uint8_t *)0x7EFBD2UL)
#define CHIPIDX3 (*(const volatile __xdata uint8_t *)0x7EFBD3UL)
#define CHIPIDX4 (*(const volatile __xdata uint8_t *)0x7EFBD4UL)
#define CHIPIDX5 (*(const volatile __xdata uint8_t *)0x7EFBD5UL)
#define CHIPIDX6 (*(const volatile __xdata uint8_t *)0x7EFBD6UL)
#define CHIPIDX7 (*(const volatile __xdata uint8_t *)0x7EFBD7UL)
#define CHIPIDX8 (*(const volatile __xdata uint8_t *)0x7EFBD8UL)
#define CHIPIDX9 (*(const volatile __xdata uint8_t *)0x7EFBD9UL)
#define CHIPIDX10 (*(const volatile __xdata uint8_t *)0x7EFBDAUL)
#define CHIPIDX11 (*(const volatile __xdata uint8_t *)0x7EFBDBUL)
#define CHIPIDX12 (*(const volatile __xdata uint8_t *)0x7EFBDCUL)
#define CHIPIDX13 (*(const volatile __xdata uint8_t *)0x7EFBDDUL)
#define CHIPIDX14 (*(const volatile __xdata uint8_t *)0x7EFBDEUL)
#define CHIPIDX15 (*(const volatile __xdata uint8_t *)0x7EFBDFUL)
#define CHIPIDX16 (*(const volatile __xdata uint8_t *)0x7EFBE0UL)
#define CHIPIDX17 (*(const volatile __xdata uint8_t *)0x7EFBE1UL)
#define CHIPIDX18 (*(const volatile __xdata uint8_t *)0x7EFBE2UL)
#define CHIPIDX19 (*(const volatile __xdata uint8_t *)0x7EFBE3UL)
#define CHIPIDX20 (*(const volatile __xdata uint8_t *)0x7EFBE4UL)
#define CHIPIDX21 (*(const volatile __xdata uint8_t *)0x7EFBE5UL)
#define CHIPIDX22 (*(const volatile __xdata uint8_t *)0x7EFBE6UL)
#define CHIPIDX23 (*(const volatile __xdata uint8_t *)0x7EFBE7UL)
#define CHIPIDX24 (*(const volatile __xdata uint8_t *)0x7EFBE8UL)
#define CHIPIDX25 (*(const volatile __xdata uint8_t *)0x7EFBE9UL)
#define CHIPIDX26 (*(const volatile __xdata uint8_t *)0x7EFBEAUL)
#define CHIPIDX27 (*(const volatile __xdata uint8_t *)0x7EFBEBUL)
#define CHIPIDX28 (*(const volatile __xdata uint8_t *)0x7EFBECUL)
#define CHIPIDX29 (*(const volatile __xdata uint8_t *)0x7EFBEDUL)
#define CHIPIDX30 (*(const volatile __xdata uint8_t *)0x7EFBEEUL)
#define CHIPIDX31 (*(const volatile __xdata uint8_t *)0x7EFBEFUL)
#define HSPWMA_CFG (*(volatile __xdata uint8_t *)0x7EFBF0UL)
#define HSPWMA_ADR (*(volatile __xdata uint8_t *)0x7EFBF1UL)
#define HSPWMA_DAT (*(volatile __xdata uint8_t *)0x7EFBF2UL)
#define HSPWMA_ADRH (*(volatile __xdata uint8_t *)0x7EFBF3UL)
#define HSPWMB_CFG (*(volatile __xdata uint8_t *)0x7EFBF4UL)
#define HSPWMB_ADR (*(volatile __xdata uint8_t *)0x7EFBF5UL)
#define HSPWMB_DAT (*(volatile __xdata uint8_t *)0x7EFBF6UL)
#define HSPWMB_ADRH (*(volatile __xdata uint8_t *)0x7EFBF7UL)
#define HSSPI_CFG (*(volatile __xdata uint8_t *)0x7EFBF8UL)
#define HSSPI_CFG2 (*(volatile __xdata uint8_t *)0x7EFBF9UL)
#define HSSPI_STA (*(volatile __xdata uint8_t *)0x7EFBFAUL)
#define HSSPI_PSCR (*(volatile __xdata uint8_t *)0x7EFBFBUL)

/* XFR 0x7EFA00 */
#define DMA_M2M_CFG (*(volatile __xdata uint8_t *)0x7EFA00UL)
#define DMA_M2M_CR (*(volatile __xdata uint8_t *)0x7EFA01UL)
#define DMA_M2M_STA (*(volatile __xdata uint8_t *)0x7EFA02UL)
#define DMA_M2M_AMT (*(volatile __xdata uint8_t *)0x7EFA03UL)
#define DMA_M2M_DONE (*(volatile __xdata uint8_t *)0x7EFA04UL)
#define DMA_M2M_TXAH (*(volatile __xdata uint8_t *)0x7EFA05UL)
#define DMA_M2M_TXAL (*(volatile __xdata uint8_t *)0x7EFA06UL)
#define DMA_M2M_RXAH (*(volatile __xdata uint8_t *)0x7EFA07UL)
#define DMA_M2M_RXAL (*(volatile __xdata uint8_t *)0x7EFA08UL)
#define DMA_ADC_CFG (*(volatile __xdata uint8_t *)0x7EFA10UL)
#define DMA_ADC_CR (*(volatile __xdata uint8_t *)0x7EFA11UL)
#define DMA_ADC_STA (*(volatile __xdata uint8_t *)0x7EFA12UL)
#define DMA_ADC_AMT (*(volatile __xdata uint8_t *)0x7EFA13UL)
#define DMA_ADC_DONE (*(volatile __xdata uint8_t *)0x7EFA14UL)
#define DMA_ADC_RXAH (*(volatile __xdata uint8_t *)0x7EFA17UL)
#define DMA_ADC_RXAL (*(volatile __xdata uint8_t *)0x7EFA18UL)
#define DMA_ADC_CFG2 (*(volatile __xdata uint8_t *)0x7EFA19UL)
#define DMA_ADC_CHSW0 (*(volatile __xdata uint8_t *)0x7EFA1AUL)
#define DMA_ADC_CHSW1 (*(volatile __xdata uint8_t *)0x7EFA1BUL)
#define DMA_ADC_ITVH (*(volatile __xdata uint8_t *)0x7EFA1EUL)
#define DMA_ADC_ITVL (*(volatile __xdata uint8_t *)0x7EFA1FUL)
#define DMA_SPI_CFG (*(volatile __xdata uint8_t *)0x7EFA20UL)
#define DMA_SPI_CR (*(volatile __xdata uint8_t *)0x7EFA21UL)
#define DMA_SPI_STA (*(volatile __xdata uint8_t *)0x7EFA22UL)
#define DMA_SPI_AMT (*(volatile __xdata uint8_t *)0x7EFA23UL)
#define DMA_SPI_DONE (*(volatile __xdata uint8_t *)0x7EFA24UL)
#define DMA_SPI_TXAH (*(volatile __xdata uint8_t *)0x7EFA25UL)
#define DMA_SPI_TXAL (*(volatile __xdata uint8_t *)0x7EFA26UL)
#define DMA_SPI_RXAH (*(volatile __xdata uint8_t *)0x7EFA27UL)
#define DMA_SPI_RXAL (*(volatile __xdata uint8_t *)0x7EFA28UL)
#define DMA_SPI_CFG2 (*(volatile __xdata uint8_t *)0x7EFA29UL)
#define DMA_SPI_ITVH (*(volatile __xdata uint8_t *)0x7EFA2EUL)
#define DMA_SPI_ITVL (*(volatile __xdata uint8_t *)0x7EFA2FUL)
#define DMA_UR1T_CFG (*(volatile __xdata uint8_t *)0x7EFA30UL)
#define DMA_UR1T_CR (*(volatile __xdata uint8_t *)0x7EFA31UL)
#define DMA_UR1T_STA (*(volatile __xdata uint8_t *)0x7EFA32UL)
#define DMA_UR1T_AMT (*(volatile __xdata uint8_t *)0x7EFA33UL)
#define DMA_UR1T_DONE (*(volatile __xdata uint8_t *)0x7EFA34UL)
#define DMA_UR1T_TXAH (*(volatile __xdata uint8_t *)0x7EFA35UL)
#define DMA_UR1T_TXAL (*(volatile __xdata uint8_t *)0x7EFA36UL)
#define DMA_UR1R_CFG (*(volatile __xdata uint8_t *)0x7EFA38UL)
#define DMA_UR1R_CR (*(volatile __xdata uint8_t *)0x7EFA39UL)
#define DMA_UR1R_STA (*(volatile __xdata uint8_t *)0x7EFA3AUL)
#define DMA_UR1R_AMT (*(volatile __xdata uint8_t *)0x7EFA3BUL)
#define DMA_UR1R_DONE (*(volatile __xdata uint8_t *)0x7EFA3CUL)
#define DMA_UR1R_RXAH (*(volatile __xdata uint8_t *)0x7EFA3DUL)
#define DMA_UR1R_RXAL (*(volatile __xdata uint8_t *)0x7EFA3EUL)
#define DMA_UR2T_CFG (*(volatile __xdata uint8_t *)0x7EFA40UL)
#define DMA_UR2T_CR (*(volatile __xdata uint8_t *)0x7EFA41UL)
#define DMA_UR2T_STA (*(volatile __xdata uint8_t *)0x7EFA42UL)
#define DMA_UR2T_AMT (*(volatile __xdata uint8_t *)0x7EFA43UL)
#define DMA_UR2T_DONE (*(volatile __xdata uint8_t *)0x7EFA44UL)
#define DMA_UR2T_TXAH (*(volatile __xdata uint8_t *)0x7EFA45UL)
#define DMA_UR2T_TXAL (*(volatile __xdata uint8_t *)0x7EFA46UL)
#define DMA_UR2R_CFG (*(volatile __xdata uint8_t *)0x7EFA48UL)
#define DMA_UR2R_CR (*(volatile __xdata uint8_t *)0x7EFA49UL)
#define DMA_UR2R_STA (*(volatile __xdata uint8_t *)0x7EFA4AUL)
#define DMA_UR2R_AMT (*(volatile __xdata uint8_t *)0x7EFA4BUL)
#define DMA_UR2R_DONE (*(volatile __xdata uint8_t *)0x7EFA4CUL)
#define DMA_UR2R_RXAH (*(volatile __xdata uint8_t *)0x7EFA4DUL)
#define DMA_UR2R_RXAL (*(volatile __xdata uint8_t *)0x7EFA4EUL)
#define DMA_UR3T_CFG (*(volatile __xdata uint8_t *)0x7EFA50UL)
#define DMA_UR3T_CR (*(volatile __xdata uint8_t *)0x7EFA51UL)
#define DMA_UR3T_STA (*(volatile __xdata uint8_t *)0x7EFA52UL)
#define DMA_UR3T_AMT (*(volatile __xdata uint8_t *)0x7EFA53UL)
#define DMA_UR3T_DONE (*(volatile __xdata uint8_t *)0x7EFA54UL)
#define DMA_UR3T_TXAH (*(volatile __xdata uint8_t *)0x7EFA55UL)
#define DMA_UR3T_TXAL (*(volatile __xdata uint8_t *)0x7EFA56UL)
#define DMA_UR3R_CFG (*(volatile __xdata uint8_t *)0x7EFA58UL)
#define DMA_UR3R_CR (*(volatile __xdata uint8_t *)0x7EFA59UL)
#define DMA_UR3R_STA (*(volatile __xdata uint8_t *)0x7EFA5AUL)
#define DMA_UR3R_AMT (*(volatile __xdata uint8_t *)0x7EFA5BUL)
#define DMA_UR3R_DONE (*(volatile __xdata uint8_t *)0x7EFA5CUL)
#define DMA_UR3R_RXAH (*(volatile __xdata uint8_t *)0x7EFA5DUL)
#define DMA_UR3R_RXAL (*(volatile __xdata uint8_t *)0x7EFA5EUL)
#define DMA_UR4T_CFG (*(volatile __xdata uint8_t *)0x7EFA60UL)
#define DMA_UR4T_CR (*(volatile __xdata uint8_t *)0x7EFA61UL)
#define DMA_UR4T_STA (*(volatile __xdata uint8_t *)0x7EFA62UL)
#define DMA_UR4T_AMT (*(volatile __xdata uint8_t *)0x7EFA63UL)
#define DMA_UR4T_DONE (*(volatile __xdata uint8_t *)0x7EFA64UL)
#define DMA_UR4T_TXAH (*(volatile __xdata uint8_t *)0x7EFA65UL)
#define DMA_UR4T_TXAL (*(volatile __xdata uint8_t *)0x7EFA66UL)
#define DMA_UR4R_CFG (*(volatile __xdata uint8_t *)0x7EFA68UL)
#define DMA_UR4R_CR (*(volatile __xdata uint8_t *)0x7EFA69UL)
#define DMA_UR4R_STA (*(volatile __xdata uint8_t *)0x7EFA6AUL)
#define DMA_UR4R_AMT (*(volatile __xdata uint8_t *)0x7EFA6BUL)
#define DMA_UR4R_DONE (*(volatile __xdata uint8_t *)0x7EFA6CUL)
#define DMA_UR4R_RXAH (*(volatile __xdata uint8_t *)0x7EFA6DUL)
#define DMA_UR4R_RXAL (*(volatile __xdata uint8_t *)0x7EFA6EUL)
#define DMA_LCM_CFG (*(volatile __xdata uint8_t *)0x7EFA70UL)
#define DMA_LCM_CR (*(volatile __xdata uint8_t *)0x7EFA71UL)
#define DMA_LCM_STA (*(volatile __xdata uint8_t *)0x7EFA72UL)
#define DMA_LCM_AMT (*(volatile __xdata uint8_t *)0x7EFA73UL)
#define DMA_LCM_DONE (*(volatile __xdata uint8_t *)0x7EFA74UL)
#define DMA_LCM_TXAH (*(volatile __xdata uint8_t *)0x7EFA75UL)
#define DMA_LCM_TXAL (*(volatile __xdata uint8_t *)0x7EFA76UL)
#define DMA_LCM_RXAH (*(volatile __xdata uint8_t *)0x7EFA77UL)
#define DMA_LCM_RXAL (*(volatile __xdata uint8_t *)0x7EFA78UL)
#define DMA_LCM_ITVH (*(volatile __xdata uint8_t *)0x7EFA7EUL)
#define DMA_LCM_ITVL (*(volatile __xdata uint8_t *)0x7EFA7FUL)
#define DMA_M2M_AMTH (*(volatile __xdata uint8_t *)0x7EFA80UL)
#define DMA_M2M_DONEH (*(volatile __xdata uint8_t *)0x7EFA81UL)
#define DMA_ADC_AMTH (*(volatile __xdata uint8_t *)0x7EFA82UL)
#define DMA_ADC_DONEH (*(volatile __xdata uint8_t *)0x7EFA83UL)
#define DMA_SPI_AMTH (*(volatile __xdata uint8_t *)0x7EFA84UL)
#define DMA_SPI_DONEH (*(volatile __xdata uint8_t *)0x7EFA85UL)
#define DMA_LCM_AMTH (*(volatile __xdata uint8_t *)0x7EFA86UL)
#define DMA_LCM_DONEH (*(volatile __xdata uint8_t *)0x7EFA87UL)
#define DMA_UR1T_AMTH (*(volatile __xdata uint8_t *)0x7EFA88UL)
#define DMA_UR1T_DONEH (*(volatile __xdata uint8_t *)0x7EFA89UL)
#define DMA_UR1R_AMTH (*(volatile __xdata uint8_t *)0x7EFA8AUL)
#define DMA_UR1R_DONEH (*(volatile __xdata uint8_t *)0x7EFA8BUL)
#define DMA_UR2T_AMTH (*(volatile __xdata uint8_t *)0x7EFA8CUL)
#define DMA_UR2T_DONEH (*(volatile __xdata uint8_t *)0x7EFA8DUL)
#define DMA_UR2R_AMTH (*(volatile __xdata uint8_t *)0x7EFA8EUL)
#define DMA_UR2R_DONEH (*(volatile __xdata uint8_t *)0x7EFA8FUL)
#define DMA_UR3T_AMTH (*(volatile __xdata uint8_t *)0x7EFA90UL)
#define DMA_UR3T_DONEH (*(volatile __xdata uint8_t *)0x7EFA91UL)
#define DMA_UR3R_AMTH (*(volatile __xdata uint8_t *)0x7EFA92UL)
#define DMA_UR3R_DONEH (*(volatile __xdata uint8_t *)0x7EFA93UL)
#define DMA_UR4T_AMTH (*(volatile __xdata uint8_t *)0x7EFA94UL)
#define DMA_UR4T_DONEH (*(volatile __xdata uint8_t *)0x7EFA95UL)
#define DMA_UR4R_AMTH (*(volatile __xdata uint8_t *)0x7EFA96UL)
#define DMA_UR4R_DONEH (*(volatile __xdata uint8_t *)0x7EFA97UL)
#define DMA_I2CT_CFG (*(volatile __xdata uint8_t *)0x7EFA98UL)
#define DMA_I2CT_CR (*(volatile __xdata uint8_t *)0x7EFA99UL)
#define DMA_I2CT_STA (*(volatile __xdata uint8_t *)0x7EFA9AUL)
#define DMA_I2CT_AMT (*(volatile __xdata uint8_t *)0x7EFA9BUL)
#define DMA_I2CT_DONE (*(volatile __xdata uint8_t *)0x7EFA9CUL)
#define DMA_I2CT_TXAH (*(volatile __xdata uint8_t *)0x7EFA9DUL)
#define DMA_I2CT_TXAL (*(volatile __xdata uint8_t *)0x7EFA9EUL)
#define DMA_I2CR_CFG (*(volatile __xdata uint8_t *)0x7EFAA0UL)
#define DMA_I2CR_CR (*(volatile __xdata uint8_t *)0x7EFAA1UL)
#define DMA_I2CR_STA (*(volatile __xdata uint8_t *)0x7EFAA2UL)
#define DMA_I2CR_AMT (*(volatile __xdata uint8_t *)0x7EFAA3UL)
#define DMA_I2CR_DONE (*(volatile __xdata uint8_t *)0x7EFAA4UL)
#define DMA_I2CR_RXAH (*(volatile __xdata uint8_t *)0x7EFAA5UL)
#define DMA_I2CR_RXAL (*(volatile __xdata uint8_t *)0x7EFAA6UL)
#define DMA_I2CT_AMTH (*(volatile __xdata uint8_t *)0x7EFAA8UL)
#define DMA_I2CT_DONEH (*(volatile __xdata uint8_t *)0x7EFAA9UL)
#define DMA_I2CR_AMTH (*(volatile __xdata uint8_t *)0x7EFAAAUL)
#define DMA_I2CR_DONEH (*(volatile __xdata uint8_t *)0x7EFAABUL)
#define DMA_I2C_CR (*(volatile __xdata uint8_t *)0x7EFAADUL)
#define DMA_I2C_ST1 (*(volatile __xdata uint8_t *)0x7EFAAEUL)
#define DMA_I2C_ST2 (*(volatile __xdata uint8_t *)0x7EFAAFUL)
#define DMA_I2ST_CFG (*(volatile __xdata uint8_t *)0x7EFAB0UL)
#define DMA_I2ST_CR (*(volatile __xdata uint8_t *)0x7EFAB1UL)
#define DMA_I2ST_STA (*(volatile __xdata uint8_t *)0x7EFAB2UL)
#define DMA_I2ST_AMT (*(volatile __xdata uint8_t *)0x7EFAB3UL)
#define DMA_I2ST_DONE (*(volatile __xdata uint8_t *)0x7EFAB4UL)
#define DMA_I2ST_TXAH (*(volatile __xdata uint8_t *)0x7EFAB5UL)
#define DMA_I2ST_TXAL (*(volatile __xdata uint8_t *)0x7EFAB6UL)
#define DMA_I2SR_CFG (*(volatile __xdata uint8_t *)0x7EFAB8UL)
#define DMA_I2SR_CR (*(volatile __xdata uint8_t *)0x7EFAB9UL)
#define DMA_I2SR_STA (*(volatile __xdata uint8_t *)0x7EFABAUL)
#define DMA_I2SR_AMT (*(volatile __xdata uint8_t *)0x7EFABBUL)
#define DMA_I2SR_DONE (*(volatile __xdata uint8_t *)0x7EFABCUL)
#define DMA_I2SR_RXAH (*(volatile __xdata uint8_t *)0x7EFABDUL)
#define DMA_I2SR_RXAL (*(volatile __xdata uint8_t *)0x7EFABEUL)
#define DMA_I2ST_AMTH (*(volatile __xdata uint8_t *)0x7EFAC0UL)
#define DMA_I2ST_DONEH (*(volatile __xdata uint8_t *)0x7EFAC1UL)
#define DMA_I2SR_AMTH (*(volatile __xdata uint8_t *)0x7EFAC2UL)
#define DMA_I2SR_DONEH (*(volatile __xdata uint8_t *)0x7EFAC3UL)
#define DMA_I2C_ITVH (*(volatile __xdata uint8_t *)0x7EFAC4UL)
#define DMA_I2C_ITVL (*(volatile __xdata uint8_t *)0x7EFAC5UL)
#define DMA_I2S_ITVH (*(volatile __xdata uint8_t *)0x7EFAC6UL)
#define DMA_I2S_ITVL (*(volatile __xdata uint8_t *)0x7EFAC7UL)
#define DMA_UR1_ITVH (*(volatile __xdata uint8_t *)0x7EFAC8UL)
#define DMA_UR1_ITVL (*(volatile __xdata uint8_t *)0x7EFAC9UL)
#define DMA_UR2_ITVH (*(volatile __xdata uint8_t *)0x7EFACAUL)
#define DMA_UR2_ITVL (*(volatile __xdata uint8_t *)0x7EFACBUL)
#define DMA_UR3_ITVH (*(volatile __xdata uint8_t *)0x7EFACCUL)
#define DMA_UR3_ITVL (*(volatile __xdata uint8_t *)0x7EFACDUL)
#define DMA_UR4_ITVH (*(volatile __xdata uint8_t *)0x7EFACEUL)
#define DMA_UR4_ITVL (*(volatile __xdata uint8_t *)0x7EFACFUL)
#define DMA_QSPI_CFG (*(volatile __xdata uint8_t *)0x7EFAD0UL)
#define DMA_QSPI_CR (*(volatile __xdata uint8_t *)0x7EFAD1UL)
#define DMA_QSPI_STA (*(volatile __xdata uint8_t *)0x7EFAD2UL)
#define DMA_QSPI_AMT (*(volatile __xdata uint8_t *)0x7EFAD3UL)
#define DMA_QSPI_DONE (*(volatile __xdata uint8_t *)0x7EFAD4UL)
#define DMA_QSPI_TXAH (*(volatile __xdata uint8_t *)0x7EFAD5UL)
#define DMA_QSPI_TXAL (*(volatile __xdata uint8_t *)0x7EFAD6UL)
#define DMA_QSPI_RXAH (*(volatile __xdata uint8_t *)0x7EFAD7UL)
#define DMA_QSPI_RXAL (*(volatile __xdata uint8_t *)0x7EFAD8UL)
#define DMA_QSPI_AMTH (*(volatile __xdata uint8_t *)0x7EFADBUL)
#define DMA_QSPI_DONEH (*(volatile __xdata uint8_t *)0x7EFADCUL)
#define DMA_QSPI_ITVH (*(volatile __xdata uint8_t *)0x7EFADEUL)
#define DMA_QSPI_ITVL (*(volatile __xdata uint8_t *)0x7EFADFUL)
#define DMA_P2P_CR1 (*(volatile __xdata uint8_t *)0x7EFAF0UL)
#define DMA_P2P_CR2 (*(volatile __xdata uint8_t *)0x7EFAF1UL)
#define DMA_ARB_CFG (*(volatile __xdata uint8_t *)0x7EFAF8UL)
#define DMA_ARB_STA (*(volatile __xdata uint8_t *)0x7EFAF9UL)

/* XFR 0x7EF900 */
#define QSPI_CR1 (*(volatile __xdata uint8_t *)0x7EF900UL)
#define QSPI_CR2 (*(volatile __xdata uint8_t *)0x7EF901UL)
#define QSPI_CR3 (*(volatile __xdata uint8_t *)0x7EF902UL)
#define QSPI_CR4 (*(volatile __xdata uint8_t *)0x7EF903UL)
#define QSPI_DCR1 (*(volatile __xdata uint8_t *)0x7EF904UL)
#define QSPI_DCR2 (*(volatile __xdata uint8_t *)0x7EF905UL)
#define QSPI_SR1 (*(volatile __xdata uint8_t *)0x7EF906UL)
#define QSPI_SR2 (*(volatile __xdata uint8_t *)0x7EF907UL)
#define QSPI_FCR (*(volatile __xdata uint8_t *)0x7EF908UL)
#define QSPI_HCR1 (*(volatile __xdata uint8_t *)0x7EF909UL)
#define QSPI_HCR2 (*(volatile __xdata uint8_t *)0x7EF90AUL)
#define QSPI_DLR1 (*(volatile __xdata uint8_t *)0x7EF910UL)
#define QSPI_DLR2 (*(volatile __xdata uint8_t *)0x7EF911UL)
#define QSPI_CCR1 (*(volatile __xdata uint8_t *)0x7EF914UL)
#define QSPI_CCR2 (*(volatile __xdata uint8_t *)0x7EF915UL)
#define QSPI_CCR3 (*(volatile __xdata uint8_t *)0x7EF916UL)
#define QSPI_CCR4 (*(volatile __xdata uint8_t *)0x7EF917UL)
#define QSPI_AR1 (*(volatile __xdata uint8_t *)0x7EF918UL)
#define QSPI_AR2 (*(volatile __xdata uint8_t *)0x7EF919UL)
#define QSPI_AR3 (*(volatile __xdata uint8_t *)0x7EF91AUL)
#define QSPI_AR4 (*(volatile __xdata uint8_t *)0x7EF91BUL)
#define QSPI_ABR (*(volatile __xdata uint8_t *)0x7EF91CUL)
#define QSPI_DR (*(volatile __xdata uint8_t *)0x7EF920UL)
#define QSPI_PSMKR1 (*(volatile __xdata uint8_t *)0x7EF924UL)
#define QSPI_PSMAR1 (*(volatile __xdata uint8_t *)0x7EF928UL)
#define QSPI_PIR1 (*(volatile __xdata uint8_t *)0x7EF92CUL)
#define QSPI_PIR2 (*(volatile __xdata uint8_t *)0x7EF92DUL)
#define PWMA_ENO2 (*(volatile __xdata uint8_t *)0x7EF930UL)
#define PWMA_IOAUX2 (*(volatile __xdata uint8_t *)0x7EF931UL)
#define PWMA_CR3 (*(volatile __xdata uint8_t *)0x7EF932UL)
#define PWMA_SR3 (*(volatile __xdata uint8_t *)0x7EF933UL)
#define PWMA_CCER3 (*(volatile __xdata uint8_t *)0x7EF934UL)
#define PWMA_CCMR1X (*(volatile __xdata uint8_t *)0x7EF938UL)
#define PWMA_CCMR2X (*(volatile __xdata uint8_t *)0x7EF939UL)
#define PWMA_CCMR3X (*(volatile __xdata uint8_t *)0x7EF93AUL)
#define PWMA_CCMR4X (*(volatile __xdata uint8_t *)0x7EF93BUL)
#define PWMA_CCMR5 (*(volatile __xdata uint8_t *)0x7EF93CUL)
#define PWMA_CCMR5X (*(volatile __xdata uint8_t *)0x7EF93DUL)
#define PWMA_CCMR6 (*(volatile __xdata uint8_t *)0x7EF93EUL)
#define PWMA_CCMR6X (*(volatile __xdata uint8_t *)0x7EF93FUL)
#define PWMA_CCR5H (*(volatile __xdata uint8_t *)0x7EF940UL)
#define PWMA_CCR5L (*(volatile __xdata uint8_t *)0x7EF941UL)
#define PWMA_CCR5X (*(volatile __xdata uint8_t *)0x7EF942UL)
#define PWMA_CCR6H (*(volatile __xdata uint8_t *)0x7EF943UL)
#define PWMA_CCR6L (*(volatile __xdata uint8_t *)0x7EF944UL)
#define PWMA_DER (*(volatile __xdata uint8_t *)0x7EF948UL)
#define PWMA_DBA (*(volatile __xdata uint8_t *)0x7EF949UL)
#define PWMA_DBL (*(volatile __xdata uint8_t *)0x7EF94AUL)
#define PWMA_DMACR (*(volatile __xdata uint8_t *)0x7EF94BUL)
#define DMA_PWMAT_CFG (*(volatile __xdata uint8_t *)0x7EF980UL)
#define DMA_PWMAT_CR (*(volatile __xdata uint8_t *)0x7EF981UL)
#define DMA_PWMAT_STA (*(volatile __xdata uint8_t *)0x7EF982UL)
#define DMA_PWMAT_AMTH (*(volatile __xdata uint8_t *)0x7EF984UL)
#define DMA_PWMAT_AMT (*(volatile __xdata uint8_t *)0x7EF985UL)
#define DMA_PWMAT_DONEH (*(volatile __xdata uint8_t *)0x7EF986UL)
#define DMA_PWMAT_DONE (*(volatile __xdata uint8_t *)0x7EF987UL)
#define DMA_PWMAT_TXAH (*(volatile __xdata uint8_t *)0x7EF988UL)
#define DMA_PWMAT_TXAL (*(volatile __xdata uint8_t *)0x7EF989UL)
#define DMA_PWMA_ITVH (*(volatile __xdata uint8_t *)0x7EF98EUL)
#define DMA_PWMA_ITVL (*(volatile __xdata uint8_t *)0x7EF98FUL)
#define DMA_PWMAR_CFG (*(volatile __xdata uint8_t *)0x7EF990UL)
#define DMA_PWMAR_CR (*(volatile __xdata uint8_t *)0x7EF991UL)
#define DMA_PWMAR_STA (*(volatile __xdata uint8_t *)0x7EF992UL)
#define DMA_PWMAR_AMTH (*(volatile __xdata uint8_t *)0x7EF994UL)
#define DMA_PWMAR_AMT (*(volatile __xdata uint8_t *)0x7EF995UL)
#define DMA_PWMAR_DONEH (*(volatile __xdata uint8_t *)0x7EF996UL)
#define DMA_PWMAR_DONE (*(volatile __xdata uint8_t *)0x7EF997UL)
#define DMA_PWMAR_RXAH (*(volatile __xdata uint8_t *)0x7EF998UL)
#define DMA_PWMAR_RXAL (*(volatile __xdata uint8_t *)0x7EF999UL)

/* XFR 0x7EFE00 */
#define RTCWEEK (*(volatile __xdata uint8_t *)0x7EFE6FUL)
#define RTCYEAR (*(volatile __xdata uint8_t *)0x7EFE70UL)
#define RTCMONTH (*(volatile __xdata uint8_t *)0x7EFE71UL)
#define RTCDAY (*(volatile __xdata uint8_t *)0x7EFE72UL)
#define RTCHOUR (*(volatile __xdata uint8_t *)0x7EFE73UL)
#define RTCMIN (*(volatile __xdata uint8_t *)0x7EFE74UL)
#define RTCSEC (*(volatile __xdata uint8_t *)0x7EFE75UL)
#define RTCSSEC (*(volatile __xdata uint8_t *)0x7EFE76UL)

/* 芯片身份区只读；使用数组下标读取时仍保持volatile。 */
#define CHIPID ((const volatile __xdata uint8_t *)0x7EFDE0UL)
#define CHIPIDX ((const volatile __xdata uint8_t *)0x7EFBD0UL)

/* STC命名的位掩码：EAXFR为0x80掩码，不能写 EAXFR = 1。 */
#define SMOD 0x80U
#define SMOD0 0x40U
#define LVDF 0x20U
#define POF 0x10U
#define GF1 0x08U
#define GF0 0x04U
#define PD 0x02U
#define IDL 0x01U
#define T1_GATE 0x80U
#define T1_CT 0x40U
#define T1_M1 0x20U
#define T1_M0 0x10U
#define T0_GATE 0x08U
#define T0_CT 0x04U
#define T0_M1 0x02U
#define T0_M0 0x01U
#define T0x12 0x80U
#define T1x12 0x40U
#define S1M0x6 0x20U
#define T2R 0x10U
#define T2_CT 0x08U
#define T2x12 0x04U
#define EXTRAM 0x02U
#define S1BRT 0x01U
#define EX4 0x40U
#define EX3 0x20U
#define EX2 0x10U
#define T2CLKO 0x04U
#define T1CLKO 0x02U
#define T0CLKO 0x01U
#define RAMTINY 0x80U
#define CPUMODE 0x40U
#define S2SM0 0x80U
#define S2SM1 0x40U
#define S2SM2 0x20U
#define S2REN 0x10U
#define S2TB8 0x08U
#define S2RB8 0x04U
#define S2TI 0x02U
#define S2RI 0x01U
#define USBCKS 0x80U
#define USBCKS2 0x40U
#define HIRCSEL1 0x02U
#define HIRCSEL0 0x01U
#define S1_S1 0x80U
#define S1_S0 0x40U
#define CAN_S1 0x20U
#define CAN_S0 0x10U
#define SPI_S1 0x08U
#define SPI_S0 0x04U
#define LIN_S1 0x02U
#define LIN_S0 0x01U
#define WKTEN 0x80U
#define S3SM0 0x80U
#define S3ST3 0x40U
#define S3SM2 0x20U
#define S3REN 0x10U
#define S3TB8 0x08U
#define S3RB8 0x04U
#define S3TI 0x02U
#define S3RI 0x01U
#define EUSB 0x80U
#define ET4 0x40U
#define ET3 0x20U
#define ES4 0x10U
#define ES3 0x08U
#define ET2 0x04U
#define ESPI 0x02U
#define ES2 0x01U
#define PUSB 0x80U
#define PI2C 0x40U
#define PCMP 0x20U
#define PX4 0x10U
#define PPWMB 0x08U
#define PPWMA 0x04U
#define PSPI 0x02U
#define PS2 0x01U
#define PUSBH 0x80U
#define PI2CH 0x40U
#define PCMPH 0x20U
#define PX4H 0x10U
#define PPWMBH 0x08U
#define PPWMAH 0x04U
#define PSPIH 0x02U
#define PS2H 0x01U
#define PPCAH 0x80U
#define PLVDH 0x40U
#define PADCH 0x20U
#define PSH 0x10U
#define PT1H 0x08U
#define PX1H 0x04U
#define PT0H 0x02U
#define PX0H 0x01U
#define EAXFR 0x80U
#define I2C_S1 0x20U
#define I2C_S0 0x10U
#define CMPO_S 0x08U
#define S4_S 0x04U
#define S3_S 0x02U
#define S2_S 0x01U
#define I2S_S1 0x80U
#define I2S_S0 0x40U
#define S2SPI_S1 0x20U
#define S2SPI_S0 0x10U
#define S1SPI_S1 0x08U
#define S1SPI_S0 0x04U
#define ADC_POWER 0x80U
#define ADC_START 0x40U
#define ADC_FLAG 0x20U
#define ADC_EPWMT 0x10U
#define QSPI_S1 0x02U
#define QSPI_S0 0x01U
#define WDT_FLAG 0x80U
#define EN_WDT 0x20U
#define CLR_WDT 0x10U
#define IDL_WDT 0x08U
#define IAPEN 0x80U
#define SWBS 0x40U
#define SWRST 0x20U
#define CMD_FAIL 0x10U
#define SWBS2 0x08U
#define SPIF 0x80U
#define WCOL 0x40U
#define SSIG 0x80U
#define SPEN 0x40U
#define DORD 0x20U
#define MSTR 0x10U
#define CPOL 0x08U
#define CPHA 0x04U
#define SPR1 0x02U
#define SPR0 0x01U
#define N 0x20U
#define Z 0x02U
#define T4R 0x80U
#define T4_CT 0x40U
#define T4x12 0x20U
#define T4CLKO 0x10U
#define T3R 0x08U
#define T3_CT 0x04U
#define T3x12 0x02U
#define T3CLKO 0x01U
#define RESFMT 0x20U
#define PI2S 0x08U
#define PRTC 0x04U
#define PS4 0x02U
#define PS3 0x01U
#define CMPEN 0x80U
#define CMPIF 0x40U
#define PIE 0x20U
#define NIE 0x10U
#define CMPOE 0x02U
#define CMPRES 0x01U
#define INVCMPO 0x80U
#define DISFLT 0x40U
#define PI2SH 0x08U
#define PRTCH 0x04U
#define PS4H 0x02U
#define PS3H 0x01U
#define INT4IF 0x40U
#define INT3IF 0x20U
#define INT2IF 0x10U
#define T4IF 0x04U
#define T3IF 0x02U
#define T2IF 0x01U
#define ENUSB 0x80U
#define ENUSBRST 0x40U
#define PS2M 0x20U
#define PUEN 0x10U
#define PDEN 0x08U
#define DFREC 0x04U
#define DP 0x02U
#define DM 0x01U
#define S4SM0 0x80U
#define S4ST4 0x40U
#define S4SM2 0x20U
#define S4REN 0x10U
#define S4TB8 0x08U
#define S4RB8 0x04U
#define S4TI 0x02U
#define S4RI 0x01U
#define ENLVR 0x40U
#define P47RST 0x10U
#define CF 0x80U
#define CR 0x40U
#define CCF2 0x04U
#define CCF1 0x02U
#define CCF0 0x01U
#define CIDL 0x80U
#define ECF 0x01U
#define UPDATE 0x80U
#define ISOUD 0x80U
#define USBRST 0x08U
#define USBRSU 0x04U
#define USBSUS 0x02U
#define ENSUS 0x01U
#define EP5INIF 0x20U
#define EP4INIF 0x10U
#define EP3INIF 0x08U
#define EP2INIF 0x04U
#define EP1INIF 0x02U
#define EP0IF 0x01U
#define EP5OUTIF 0x20U
#define EP4OUTIF 0x10U
#define EP3OUTIF 0x08U
#define EP2OUTIF 0x04U
#define EP1OUTIF 0x02U
#define SOFIF 0x08U
#define RSTIF 0x04U
#define RSUIF 0x02U
#define SUSIF 0x01U
#define EP5INIE 0x20U
#define EP4INIE 0x10U
#define EP3INIE 0x08U
#define EP2INIE 0x04U
#define EP1INIE 0x02U
#define EP0IE 0x01U
#define EP5OUTIE 0x20U
#define EP4OUTIE 0x10U
#define EP3OUTIE 0x08U
#define EP2OUTIE 0x04U
#define EP1OUTIE 0x02U
#define SOFIE 0x08U
#define RSTIE 0x04U
#define RSUIE 0x02U
#define SUSIE 0x01U
#define SSUEND 0x80U
#define SOPRDY 0x40U
#define SDSTL 0x20U
#define SUEND 0x10U
#define DATEND 0x08U
#define STSTL 0x04U
#define IPRDY 0x02U
#define OPRDY 0x01U
#define INCLRDT 0x40U
#define INSTSTL 0x20U
#define INSDSTL 0x10U
#define INFLUSH 0x08U
#define INUNDRUN 0x04U
#define INFIFONE 0x02U
#define INIPRDY 0x01U
#define INAUTOSET 0x80U
#define INISO 0x40U
#define INMODEIN 0x20U
#define INMODEOUT 0x00U
#define INENDMA 0x10U
#define INFCDT 0x08U
#define OUTCLRDT 0x80U
#define OUTSTSTL 0x40U
#define OUTSDSTL 0x20U
#define OUTFLUSH 0x10U
#define OUTDATERR 0x08U
#define OUTOVRRUN 0x04U
#define OUTFIFOFUL 0x02U
#define OUTOPRDY 0x01U
#define OUTAUTOCLR 0x80U
#define OUTISO 0x40U
#define OUTENDMA 0x20U
#define OUTDMAMD 0x10U

/* USB内部寄存器是USBADR/USBDAT间接访问的偏移量，不是存储器左值。 */
#define USBBASE 0U
#define FADDR 0x00U
#define POWER 0x01U
#define INTRIN1 0x02U
#define INTROUT1 0x04U
#define INTRUSB 0x06U
#define INTRIN1E 0x07U
#define INTROUT1E 0x09U
#define INTRUSBE 0x0BU
#define FRAME1 0x0CU
#define FRAME2 0x0DU
#define INDEX 0x0EU
#define INMAXP 0x10U
#define CSR0 0x11U
#define INCSR1 0x11U
#define INCSR2 0x12U
#define OUTMAXP 0x13U
#define OUTCSR1 0x14U
#define OUTCSR2 0x15U
#define COUNT0 0x16U
#define OUTCOUNT1 0x16U
#define OUTCOUNT2 0x17U
#define FIFO0 0x20U
#define FIFO1 0x21U
#define FIFO2 0x22U
#define FIFO3 0x23U
#define FIFO4 0x24U
#define FIFO5 0x25U
#define UTRKCTL 0x30U
#define UTRKSTS 0x31U

/* 中断号是芯片定义；声明这些常量不等同于全部ISR ABI已通过实板验收。 */
#define INT0_VECTOR 0U
#define TMR0_VECTOR 1U
#define INT1_VECTOR 2U
#define TMR1_VECTOR 3U
#define UART1_VECTOR 4U
#define ADC_VECTOR 5U
#define LVD_VECTOR 6U
#define PCA_VECTOR 7U
#define UART2_VECTOR 8U
#define SPI_VECTOR 9U
#define INT2_VECTOR 10U
#define INT3_VECTOR 11U
#define TMR2_VECTOR 12U
#define USER_VECTOR 13U
#define INT4_VECTOR 16U
#define UART3_VECTOR 17U
#define UART4_VECTOR 18U
#define TMR3_VECTOR 19U
#define TMR4_VECTOR 20U
#define CMP_VECTOR 21U
#define I2C_VECTOR 24U
#define USB_VECTOR 25U
#define PWMA_VECTOR 26U
#define PWMB_VECTOR 27U
#define RTC_VECTOR 36U
#define P0INT_VECTOR 37U
#define P1INT_VECTOR 38U
#define P2INT_VECTOR 39U
#define P3INT_VECTOR 40U
#define P4INT_VECTOR 41U
#define P5INT_VECTOR 42U
#define P6INT_VECTOR 43U
#define P7INT_VECTOR 44U
#define DMA_M2M_VECTOR 47U
#define DMA_ADC_VECTOR 48U
#define DMA_SPI_VECTOR 49U
#define DMA_UR1T_VECTOR 50U
#define DMA_UR1R_VECTOR 51U
#define DMA_UR2T_VECTOR 52U
#define DMA_UR2R_VECTOR 53U
#define DMA_UR3T_VECTOR 54U
#define DMA_UR3R_VECTOR 55U
#define DMA_UR4T_VECTOR 56U
#define DMA_UR4R_VECTOR 57U
#define DMA_LCM_VECTOR 58U
#define LCM_VECTOR 59U
#define DMA_I2CT_VECTOR 60U
#define DMA_I2CR_VECTOR 61U
#define I2S_VECTOR 62U
#define DMA_I2ST_VECTOR 63U
#define DMA_I2SR_VECTOR 64U
#define DMA_QSPI_VECTOR 65U
#define QSPI_VECTOR 66U
#define TMR11_VECTOR 67U
#define DMA_PWMAT_VECTOR 72U
#define DMA_PWMAR_VECTOR 73U

/* 数学单元命令值。DMAIR必须用MOV direct,#立即数触发。
 * 操作数和结果使用CPU寄存器，不能把DMAIR赋值当作普通C数学函数。 */
#define AI8051U_DMA_CMD_MUL32 0x02U
#define AI8051U_DMA_CMD_UDIV32 0x04U
#define AI8051U_DMA_CMD_SDIV32 0x06U
#define AI8051U_DMA_CMD_FADD 0x1CU
#define AI8051U_DMA_CMD_FSUB 0x1DU
#define AI8051U_DMA_CMD_FMUL 0x1EU
#define AI8051U_DMA_CMD_FDIV 0x1FU
#define AI8051U_DMA_CMD_FSQRT 0x20U
#define AI8051U_DMA_CMD_FCMP 0x21U
#define AI8051U_DMA_CMD_FCLASS 0x22U
#define AI8051U_DMA_CMD_FTOI8 0x23U
#define AI8051U_DMA_CMD_FTOI16 0x24U
#define AI8051U_DMA_CMD_FTOI32 0x25U
#define AI8051U_DMA_CMD_I8TOF 0x27U
#define AI8051U_DMA_CMD_I16TOF 0x28U
#define AI8051U_DMA_CMD_I32TOF 0x29U
#define AI8051U_DMA_CMD_FSIN 0x2DU
#define AI8051U_DMA_CMD_FCOS 0x2EU
#define AI8051U_DMA_CMD_FTAN 0x2FU
#define AI8051U_DMA_CMD_FATAN 0x30U
#define AI8051U_DMA_CMD_TFPU_INIT 0x31U
#define AI8051U_DMA_CMD_TFPU_CLEAR_EXCEPTIONS 0x32U
#define AI8051U_DMA_CMD_TFPU_READ_STATUS 0x33U
#define AI8051U_DMA_CMD_TFPU_WRITE_STATUS 0x34U
#define AI8051U_DMA_CMD_TFPU_READ_CONTROL 0x35U
#define AI8051U_DMA_CMD_TFPU_WRITE_CONTROL 0x36U
#define AI8051U_DMA_CMD_TFPU_CLOCK_SYS 0x3EU
#define AI8051U_DMA_CMD_TFPU_CLOCK_PLL 0x3FU
#define AI8051U_DMA_CMD_DPU_FILL_DPUCFG 0x80U
#define AI8051U_DMA_CMD_DPU_BIN2BCD 0x81U
#define AI8051U_DMA_CMD_DPU_BCD2BIN 0x82U
#define AI8051U_DMA_CMD_DPU_NRM_BX 0x83U
#define AI8051U_DMA_CMD_DPU_NRM_EBX 0x84U
#define AI8051U_DMA_CMD_DPU_NRM_EABX 0x85U
#define AI8051U_DMA_CMD_DPU_SWAP_EAB 0x86U
#define AI8051U_DMA_CMD_DPU_SWAP_ECD 0x87U
#define AI8051U_DMA_CMD_DPU_SWAP_EAC 0x88U
#define AI8051U_DMA_CMD_DPU_SWAP_EBD 0x89U
#define AI8051U_DMA_CMD_DPU_ADDC_EABX 0x90U
#define AI8051U_DMA_CMD_DPU_ADDC_ABX 0x91U
#define AI8051U_DMA_CMD_DPU_ADD_EABX 0x92U
#define AI8051U_DMA_CMD_DPU_ADD_ABX 0x93U
#define AI8051U_DMA_CMD_DPU_SUBB_EABX 0x94U
#define AI8051U_DMA_CMD_DPU_SUBB_ABX 0x95U
#define AI8051U_DMA_CMD_DPU_SUB_EABX 0x96U
#define AI8051U_DMA_CMD_DPU_SUB_ABX 0x97U
#define AI8051U_DMA_CMD_DPU_CMP_EABX 0x98U
#define AI8051U_DMA_CMD_DPU_CMP_ABX 0x99U
#define AI8051U_DMA_CMD_DPU_MULU_EABX 0x9AU
#define AI8051U_DMA_CMD_DPU_MULS_EABX 0x9BU
#define AI8051U_DMA_CMD_DPU_MULU_ABX 0x9CU
#define AI8051U_DMA_CMD_DPU_MULS_ABX 0x9DU
#define AI8051U_DMA_CMD_DPU_MULX_KEIL16 0x9EU
#define AI8051U_DMA_CMD_DPU_DIVU_EABX 0x9FU
#define AI8051U_DMA_CMD_DPU_DIVS_EABX 0xA0U
#define AI8051U_DMA_CMD_DPU_DIVU_ABX 0xA1U
#define AI8051U_DMA_CMD_DPU_DIVU_KEIL16 0xA2U
#define AI8051U_DMA_CMD_DPU_DIVS_ABX 0xA3U
#define AI8051U_DMA_CMD_DPU_DIVS_KEIL16 0xA4U
#define AI8051U_DMA_CMD_DPU_DIVU_EABXD 0xA5U
#define AI8051U_DMA_CMD_DPU_DIVU_EAXB 0xA6U
#define AI8051U_DMA_CMD_DPU_SET0_EAX 0xB0U
#define AI8051U_DMA_CMD_DPU_SET1_EAX 0xB1U
#define AI8051U_DMA_CMD_DPU_SET0_EBX 0xB2U
#define AI8051U_DMA_CMD_DPU_SET1_EBX 0xB3U
#define AI8051U_DMA_CMD_DPU_SET0_ECX 0xB4U
#define AI8051U_DMA_CMD_DPU_SET1_ECX 0xB5U
#define AI8051U_DMA_CMD_DPU_SET0_EDX 0xB6U
#define AI8051U_DMA_CMD_DPU_SET1_EDX 0xB7U
#define AI8051U_DMA_CMD_DPU_NEGS_EAX 0xB8U
#define AI8051U_DMA_CMD_DPU_NEGS_EBX 0xB9U
#define AI8051U_DMA_CMD_DPU_NEGS_AX 0xBAU
#define AI8051U_DMA_CMD_DPU_NEGS_BX 0xBBU
#define AI8051U_DMA_CMD_DPU_INC1_EAX 0xC0U
#define AI8051U_DMA_CMD_DPU_INC1_EBX 0xC1U
#define AI8051U_DMA_CMD_DPU_INC4_EAX 0xC2U
#define AI8051U_DMA_CMD_DPU_INC4_EBX 0xC3U
#define AI8051U_DMA_CMD_DPU_INC1_AX 0xC4U
#define AI8051U_DMA_CMD_DPU_INC1_BX 0xC5U
#define AI8051U_DMA_CMD_DPU_INC2_AX 0xC6U
#define AI8051U_DMA_CMD_DPU_INC2_BX 0xC7U
#define AI8051U_DMA_CMD_DPU_DEC1_EAX 0xC8U
#define AI8051U_DMA_CMD_DPU_DEC1_EBX 0xC9U
#define AI8051U_DMA_CMD_DPU_DEC4_EAX 0xCAU
#define AI8051U_DMA_CMD_DPU_DEC4_EBX 0xCBU
#define AI8051U_DMA_CMD_DPU_DEC1_AX 0xCCU
#define AI8051U_DMA_CMD_DPU_DEC1_BX 0xCDU
#define AI8051U_DMA_CMD_DPU_DEC2_AX 0xCEU
#define AI8051U_DMA_CMD_DPU_DEC2_BX 0xCFU
#define AI8051U_DMA_CMD_DPU_BAND_EABX 0xD0U
#define AI8051U_DMA_CMD_DPU_BAND_ABX 0xD1U
#define AI8051U_DMA_CMD_DPU_BOR_EABX 0xD2U
#define AI8051U_DMA_CMD_DPU_BOR_ABX 0xD3U
#define AI8051U_DMA_CMD_DPU_BXOR_EABX 0xD4U
#define AI8051U_DMA_CMD_DPU_BXOR_ABX 0xD5U
#define AI8051U_DMA_CMD_DPU_BCPL_EAX 0xD6U
#define AI8051U_DMA_CMD_DPU_BCPL_EBX 0xD7U
#define AI8051U_DMA_CMD_DPU_SHL_EAX 0xE0U
#define AI8051U_DMA_CMD_DPU_SHL_EBX 0xE1U
#define AI8051U_DMA_CMD_DPU_SHL_AX 0xE2U
#define AI8051U_DMA_CMD_DPU_SHL_BX 0xE3U
#define AI8051U_DMA_CMD_DPU_SHRU_EAX 0xE4U
#define AI8051U_DMA_CMD_DPU_SHRU_EBX 0xE5U
#define AI8051U_DMA_CMD_DPU_SHRU_AX 0xE6U
#define AI8051U_DMA_CMD_DPU_SHRU_BX 0xE7U
#define AI8051U_DMA_CMD_DPU_SHRS_EAX 0xE8U
#define AI8051U_DMA_CMD_DPU_SHRS_EBX 0xE9U
#define AI8051U_DMA_CMD_DPU_SHRS_AX 0xEAU
#define AI8051U_DMA_CMD_DPU_SHRS_BX 0xEBU
#define AI8051U_DMA_CMD_DPU_ROR_EAX 0xECU
#define AI8051U_DMA_CMD_DPU_ROR_EBX 0xEDU
#define AI8051U_DMA_CMD_DPU_ROR_AX 0xEEU
#define AI8051U_DMA_CMD_DPU_ROR_BX 0xEFU
#define AI8051U_DMA_CMD_DPU_MMD32_EABX 0xF0U
#define AI8051U_DMA_CMD_DPU_MMD16_ABX 0xF1U
#define AI8051U_DMA_CMD_DPU_LTC32_EAX 0xF2U
#define AI8051U_DMA_CMD_DPU_LTC16_AX 0xF3U
#define AI8051U_DMA_CMD_DPU_MA32_EDX 0xF4U
#define AI8051U_DMA_CMD_DPU_MA64_ECDX 0xF5U

/* 显式启停XFR访问；do/while保证可安全放在if/else语句中。 */
#define EAXSFR()                                                                                   \
    do                                                                                             \
    {                                                                                              \
        P_SW2 |= EAXFR;                                                                            \
    } while (0)
#define EAXRAM()                                                                                   \
    do                                                                                             \
    {                                                                                              \
        P_SW2 &= (uint8_t)~EAXFR;                                                                  \
    } while (0)

/* NOP数量是指令条数，不是微秒；使用MX251内联汇编，不依赖Keil intrins.h。 */
#define NOP1()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        __asm nop __endasm;                                                                        \
    } while (0)
#define NOP2()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP1();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP3()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP2();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP4()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP3();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP5()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP4();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP6()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP5();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP7()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP6();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP8()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP7();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP9()                                                                                     \
    do                                                                                             \
    {                                                                                              \
        NOP8();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP10()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP9();                                                                                    \
        NOP1();                                                                                    \
    } while (0)
#define NOP11()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP10();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP12()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP11();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP13()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP12();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP14()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP13();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP15()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP14();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP16()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP15();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP17()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP16();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP18()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP17();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP19()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP18();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP20()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP19();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP21()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP20();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP22()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP21();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP23()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP22();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP24()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP23();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP25()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP24();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP26()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP25();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP27()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP26();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP28()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP27();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP29()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP28();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP30()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP29();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP31()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP30();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP32()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP31();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP33()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP32();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP34()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP33();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP35()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP34();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP36()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP35();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP37()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP36();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP38()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP37();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP39()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP38();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP40()                                                                                    \
    do                                                                                             \
    {                                                                                              \
        NOP39();                                                                                   \
        NOP1();                                                                                    \
    } while (0)
#define NOP(N) NOP##N()

#include "AI8051U_Delay.h"

#endif
