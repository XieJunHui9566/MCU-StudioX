#include "ai8051u_test.h"
__xdata u8 packet[64];u8 transport_fault;
void transport_poll(void){}
u8 transport_ready(void){return 1;}
void transport_send(void){u8 i;u16 left;for(i=0;i<64;++i){TI=0;SBUF=packet[i];left=65535U;while(!TI){if(!--left){transport_fault=1;return;}}TI=0;}}
void transport_init(void)
{
    EA=0;
    P0=0; P0M1=0; P0M0=0xFF;
    P2=0xFF; P2M1=0; P2M0=0xFF; P2=0x7E;
    P3|=3; P3M1&=0xFC; P3M0=(P3M0&0xFC)|2;
    P_SW1&=0x3F; AUXR&=0x67; SCON=0x40;
    T2L=0xA9; T2H=0xFF; AUXR|=0x15;
    P_SW2|=0x80; TM0PS=0;
    /* 手册建议 XRAM 等待 0；本次固定 40 MHz，程序等待也固定为 0。 */
    WTST=0; CKCON=0;
    TR0=0; TMOD=(TMOD&0xF0)|1;

}
