#ifndef STC32G_TEST_H
#define STC32G_TEST_H
#include <stdint.h>
typedef uint8_t u8; typedef uint16_t u16; typedef uint32_t u32;
__sfr __at(0xfc) USBADR;
__sfr __at(0xec) USBDAT;
__sfr __at(0xf4) USBCON;
__sfr __at(0xdc) USBCLK;
__sfr __at(0x9d) IRCBAND;
__sfr __at(0xba) P_SW2;
__sfr __at(0xe9) WTST;
__sfr __at(0xea) CKCON;
__sfr __at(0xb2) P3M0;
__sfr __at(0xb1) P3M1;
__sfr __at(0x8e) AUXR;
__sfr __at(0x89) TMOD;
__sfr __at(0x8c) TH0;
__sfr __at(0x8a) TL0;
__sbit __at(0xaf) EA;
__sbit __at(0xa9) ET0;
__sbit __at(0x8c) TR0;
__sbit __at(0x8d) TF0;
#define IRC48MCR (*(volatile __xdata u8 *)0x7efe07UL)
#define TM0PS (*(volatile __xdata u8 *)0x7efea0UL)
#define     USBBASE                 0
#define     FADDR                   (USBBASE + 0)
#define     POWER                   (USBBASE + 1)
#define     INTRIN1                 (USBBASE + 2)
#define     INTROUT1                (USBBASE + 4)
#define     INTRUSB                 (USBBASE + 6)
#define     INTRIN1E                (USBBASE + 7)
#define     INTROUT1E               (USBBASE + 9)
#define     INTRUSBE                (USBBASE + 11)
#define     INDEX                   (USBBASE + 14)
#define INCSR2 18
#define FIFO2 34
#define     INMAXP                  (USBBASE + 16)
#define     CSR0                    (USBBASE + 17)
#define     INCSR1                  (USBBASE + 17)
#define     OUTMAXP                 (USBBASE + 19)
#define     OUTCSR1                 (USBBASE + 20)
#define     COUNT0                  (USBBASE + 22)
#define     FIFO0                   (USBBASE + 32)
#define     FIFO1                   (USBBASE + 33)
extern __xdata u8 usb_packet[64];
extern u8 usb_fault;
void usb_init(void); void usb_poll(void); u8 usb_ready(void); void usb_send(void);
#endif
