#include "stc32g_test.h"
#include "vectors.h"
/* Both application ABIs are explicit at every translation-unit boundary. */
u32 m_c_pair(u32,u32) __reentrant;
u32 m_c_native(u32,u32) __reentrant;
u32 m_c_recur(u32,u32) __reentrant;
u32 m_c_alias(u32,u32) __reentrant;
u32 m_c_many(u32,u32) __reentrant;
u32 m_c_pointer(u32,u32) __reentrant;
u32 m_c_float(u32,u32) __reentrant;
u32 m_c_pressure(u32,u32) __reentrant;
u32 m_c_frame(u32,u32) __reentrant;
u32 m_c_callback(u32,u32) __reentrant;
u32 n_c_pair(u32,u32) __reentrant __sdcccall(4);
u32 n_c_native(u32,u32) __reentrant __sdcccall(4);
u32 n_c_recur(u32,u32) __reentrant __sdcccall(4);
u32 n_c_alias(u32,u32) __reentrant __sdcccall(4);
u32 n_c_many(u32,u32) __reentrant __sdcccall(4);
u32 n_c_pointer(u32,u32) __reentrant __sdcccall(4);
u32 n_c_float(u32,u32) __reentrant __sdcccall(4);
u32 n_c_pressure(u32,u32) __reentrant __sdcccall(4);
u32 n_c_frame(u32,u32) __reentrant __sdcccall(4);
u32 n_c_callback(u32,u32) __reentrant __sdcccall(4);
static volatile __data u16 irq_count,irq_bad;
static volatile __data u32 initialized=0x12345678UL;
static volatile __xdata u32 initialized_x=0x89abcdefUL,zero_x;
static u16 boot_checks,sequence,index;
static __xdata u32 results[10],timed[60];
static __xdata u16 elapsed[60];
static __xdata u8 over[60];
static u8 timing_ready;

void timer_isr(void) __interrupt(1)
{
    TR0=0;TF0=0;TH0=0xc0;TL0=0;
    if(m_c_pair(0x800001ffUL,0xfedcba07UL)!=0x97531197UL)irq_bad|=1U;
    if(m_c_native(0x800001ffUL,0xfedcba07UL)!=0x7db979ffUL)irq_bad|=1U;
    if(m_c_recur(0x800001ffUL,0xfedcba07UL)!=0xeea3cf50UL)irq_bad|=1U;
    if(m_c_alias(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=1U;
    if(m_c_many(0x800001ffUL,0xfedcba07UL)!=0x7e738309UL)irq_bad|=1U;
    if(m_c_pointer(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=1U;
    if(m_c_float(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=1U;
    if(m_c_pressure(0x800001ffUL,0xfedcba07UL)!=0xd17d7a58UL)irq_bad|=1U;
    if(m_c_frame(0x800001ffUL,0xfedcba07UL)!=0x6ce8ea7eUL)irq_bad|=1U;
    if(m_c_callback(0x800001ffUL,0xfedcba07UL)!=0x76e5d5c5UL)irq_bad|=1U;
    if(n_c_pair(0x800001ffUL,0xfedcba07UL)!=0x97531197UL)irq_bad|=2U;
    if(n_c_native(0x800001ffUL,0xfedcba07UL)!=0x7db979ffUL)irq_bad|=2U;
    if(n_c_recur(0x800001ffUL,0xfedcba07UL)!=0xeea3cf50UL)irq_bad|=2U;
    if(n_c_alias(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=2U;
    if(n_c_many(0x800001ffUL,0xfedcba07UL)!=0x7e738309UL)irq_bad|=2U;
    if(n_c_pointer(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=2U;
    if(n_c_float(0x800001ffUL,0xfedcba07UL)!=0xfedcba07UL)irq_bad|=2U;
    if(n_c_pressure(0x800001ffUL,0xfedcba07UL)!=0xd17d7a58UL)irq_bad|=2U;
    if(n_c_frame(0x800001ffUL,0xfedcba07UL)!=0x6ce8ea7eUL)irq_bad|=2U;
    if(n_c_callback(0x800001ffUL,0xfedcba07UL)!=0x76e5d5c5UL)irq_bad|=2U;
    ++irq_count;TR0=1;
}
static void boot_check(void)
{
    static u16 local_static=0x4356;
    u32 local=0x76543210UL;u32 *ptr=&local;
    if(initialized==0x12345678UL)boot_checks|=1;
    if(initialized_x==0x89abcdefUL)boot_checks|=2;
    if(!zero_x)boot_checks|=4;
    if(!irq_count && !irq_bad)boot_checks|=8;
    if(local_static==0x4356)boot_checks|=16;
    if(*ptr==0x76543210UL)boot_checks|=32;
    if((u32)ptr>=0x400UL && (u32)ptr<0x800UL)boot_checks|=64;
    if(inputs[0][0]==0 && inputs[3][0]==1)boot_checks|=128;
}
static void start_irq(void)
{
    EA=0;TR0=0;TM0PS=0;AUXR|=0x80;TMOD=(TMOD&0xf0)|1;
    TH0=0xc0;TL0=0;TF0=0;ET0=1;TR0=1;EA=1;
}
static void measure(void)
{
    u16 j;u8 sample,t;u32 a,b,v;
    /* Fixed R84 timing input/repetitions; /12 timer, IRQ off, packaging after stop. */
    EA=0;ET0=0;TR0=0;AUXR&=0x7f;
    for(sample=0;sample<3;++sample){
        t=0U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_pair(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=3U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_native(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=6U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_recur(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=9U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_alias(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=12U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_many(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=15U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_pointer(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=18U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_float(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=21U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_pressure(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=24U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_frame(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=27U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=m_c_callback(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=30U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_pair(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=33U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_native(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=36U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_recur(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=39U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_alias(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=42U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_many(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=45U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_pointer(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=48U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_float(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=51U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_pressure(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=54U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_frame(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    for(sample=0;sample<3;++sample){
        t=57U+sample;a=0x12345678UL;b=0x89abcdefUL;
        TL0=0;TH0=0;TF0=0;TR0=1;
        for(j=0;j<64U;++j)v=n_c_callback(a,b);
        TR0=0;elapsed[t]=((u16)TH0<<8)|TL0;over[t]=TF0;timed[t]=v;
    }
    timing_ready=1;start_irq();
}
static void compute(void)
{
    u32 a,b;u16 count,bad;u8 j,p,checksum,t;
    if(index<256U){
        a=inputs[index&127U][0];b=inputs[index&127U][1];
        if(index<128U){
            results[0]=m_c_pair(a,b);
            results[1]=m_c_native(a,b);
            results[2]=m_c_recur(a,b);
            results[3]=m_c_alias(a,b);
            results[4]=m_c_many(a,b);
            results[5]=m_c_pointer(a,b);
            results[6]=m_c_float(a,b);
            results[7]=m_c_pressure(a,b);
            results[8]=m_c_frame(a,b);
            results[9]=m_c_callback(a,b);
        }else{
            results[0]=n_c_pair(a,b);
            results[1]=n_c_native(a,b);
            results[2]=n_c_recur(a,b);
            results[3]=n_c_alias(a,b);
            results[4]=n_c_many(a,b);
            results[5]=n_c_pointer(a,b);
            results[6]=n_c_float(a,b);
            results[7]=n_c_pressure(a,b);
            results[8]=n_c_frame(a,b);
            results[9]=n_c_callback(a,b);
        }
    }else{
        if(!timing_ready)measure();
        t=(u8)(index-256U);
        for(j=0;j<10;++j)results[j]=0;
        results[0]=timed[t];results[1]=elapsed[t];results[2]=over[t];
        results[3]=64;results[4]=(t%30U)/3U;results[5]=t%3U;results[6]=t/30U;
    }
    for(j=0;j<64;j++)usb_packet[j]=0;
    usb_packet[0]='M';usb_packet[1]='X';usb_packet[2]='C';usb_packet[3]='1';
    usb_packet[4]=(u8)sequence;usb_packet[5]=(u8)(sequence>>8);
    usb_packet[6]=(u8)index;usb_packet[7]=(u8)(index>>8);p=8;
    for(j=0;j<10;j++){
        a=results[j];usb_packet[p++]=(u8)a;usb_packet[p++]=(u8)(a>>8);
        usb_packet[p++]=(u8)(a>>16);usb_packet[p++]=(u8)(a>>24);
    }
    /* A coherent ISR snapshot; timing never overlaps these foreground copies. */
    EA=0;count=irq_count;bad=irq_bad;EA=1;
    usb_packet[48]=(u8)count;usb_packet[49]=(u8)(count>>8);usb_packet[50]=(u8)bad;
    usb_packet[51]=(u8)boot_checks;usb_packet[52]=(u8)(boot_checks>>8);usb_packet[53]=usb_fault;
    usb_packet[54]=1;usb_packet[55]=CALL_MODEL;
    usb_packet[56]=0x27;usb_packet[57]=0x10;usb_packet[58]=0x32;usb_packet[59]=0x13;
    checksum=0;for(j=0;j<63;j++)checksum^=usb_packet[j];usb_packet[63]=checksum;
    ++sequence;if(++index==316U)index=0;
}
void main(void)
{
    EA=0;WTST=0;CKCON=0;P_SW2|=0x80;
    boot_check();usb_init();start_irq();
    for(;;){usb_poll();if(usb_ready()){compute();usb_poll();if(usb_ready())usb_send();}}
}
