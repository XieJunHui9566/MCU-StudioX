#include "stc32g_test.h"
#include "native-int.h"
#include "vectors.h"
u32 imul(u32,u32) __reentrant;
u32 idiv(u32,u32) __reentrant;
u32 imod(u32,u32) __reentrant;
long sdiv(long,long) __reentrant;
long smod(long,long) __reentrant;
static volatile __data u16 irq_count;
static volatile __data u8 irq_bad;
static volatile __data u32 initialized=0x12345678UL;
static volatile __xdata u32 initialized_x=0x89abcdefUL;
static volatile __xdata u32 zero_x;
static u16 boot_checks;
static __xdata u32 results[10];
static u16 sequence,index;

void timer_isr(void) __interrupt(1)
{
    u8 k=(u8)irq_count&7;u16 p=(u16)k*5;u32 a,b;
    TR0=0;TF0=0;TH0=0xc0;TL0=0;
    a=irqx[k];b=irqy[k];
    if(imul(a,b)!=irqexpect[p++])irq_bad|=1;
    if(idiv(a,b)!=irqexpect[p++])irq_bad|=2;
    if(imod(a,b)!=irqexpect[p++])irq_bad|=4;
    if((u32)sdiv((long)a,(long)b)!=irqexpect[p++])irq_bad|=8;
    if((u32)smod((long)a,(long)b)!=irqexpect[p++])irq_bad|=16;
    ++irq_count;TR0=1;
}
static void boot_check(void)
{
    static u16 local_static=0x4356;
    u32 local=0x76543210UL;
    u32 *ptr=&local;
    boot_checks=0;
    if(initialized==0x12345678UL)boot_checks|=1;
    if(initialized_x==0x89abcdefUL)boot_checks|=2;
    if(!zero_x)boot_checks|=4;
    if(!irq_count && !irq_bad)boot_checks|=8;
    if(local_static==0x4356)boot_checks|=16;
    if(*ptr==0x76543210UL)boot_checks|=32;
    if((u32)ptr>=0x400UL && (u32)ptr<0x800UL)boot_checks|=64;
    if(xs[0]==0 && xs[16]==1)boot_checks|=128;
}
static void compute(void)
{
    u32 a,b,nz;long sb;u8 j,p,checksum;u16 count;
    a=xs[index];b=ys[index];nz=b?b:1;sb=(long)b;
    if(!b || (a==0x80000000UL && b==0xffffffffUL))sb=1;
    results[0]=imul(a,b);results[1]=idiv(a,nz);results[2]=imod(a,nz);
    results[3]=(u32)sdiv((long)a,sb);results[4]=(u32)smod((long)a,sb);
    results[5]=(u32)_mullong((long)a,(long)b);results[6]=_divulong(a,b);results[7]=_modulong(a,b);
    results[8]=(u32)_divslong((long)a,(long)b);results[9]=(u32)_modslong((long)a,(long)b);
    for(j=0;j<64;j++)usb_packet[j]=0;
    usb_packet[0]='M';usb_packet[1]='X';usb_packet[2]='G';usb_packet[3]='1';
    usb_packet[4]=(u8)sequence;usb_packet[5]=(u8)(sequence>>8);
    usb_packet[6]=(u8)index;usb_packet[7]=(u8)(index>>8);
    p=8;
    for(j=0;j<10;j++){
        a=results[j];usb_packet[p++]=(u8)a;usb_packet[p++]=(u8)(a>>8);
        usb_packet[p++]=(u8)(a>>16);usb_packet[p++]=(u8)(a>>24);
    }
    EA=0;count=irq_count;EA=1;
    usb_packet[48]=(u8)count;usb_packet[49]=(u8)(count>>8);usb_packet[50]=irq_bad;
    usb_packet[51]=(u8)boot_checks;usb_packet[52]=(u8)(boot_checks>>8);usb_packet[53]=usb_fault;
    usb_packet[54]=1;usb_packet[56]=0x27;usb_packet[57]=0x10;usb_packet[58]=0x32;usb_packet[59]=0x01;
    checksum=0;for(j=0;j<63;j++)checksum^=usb_packet[j];usb_packet[63]=checksum;
    ++sequence;if(++index==640)index=0;
}
void main(void)
{
    /* STC32G 专用启动代码已在 C 初始化之前关闭 IE/IE2。
       本工程只启用 Timer0，USB 轮询；不要依赖下载器交接时的复位状态。 */
    EA=0;WTST=0;CKCON=0;P_SW2|=0x80;
    boot_check();usb_init();
    TM0PS=0;AUXR|=0x80;TMOD=(TMOD&0xf0)|1;
    TH0=0xc0;TL0=0;TF0=0;ET0=1;TR0=1;EA=1;
    for(;;){usb_poll();if(usb_ready()){compute();usb_poll();if(usb_ready())usb_send();}}
}
