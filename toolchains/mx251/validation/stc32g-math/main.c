#include "stc32g_test.h"
#include "math_port.h"
#include "math_vectors.h"
#define SAMPLES 3U
#define REPEATS 8U
#define RECORDS (VECTOR_COUNT*SAMPLES)
/* 只缓存第一次执行。重复 HID 帧不是新的数值样本或计时采样。 */
static u8 XDATA cache[RECORDS][12];
static u16 completed, current, sequence;
static void put32(u8 offset,u32 value)
{
    usb_packet[offset]=(u8)value;usb_packet[offset+1]=(u8)(value>>8);
    usb_packet[offset+2]=(u8)(value>>16);usb_packet[offset+3]=(u8)(value>>24);
}
static void measure(u16 rec,u16 vec)
{
    u32 input,result,value;u8 n,j,hi,lo,overflow;kernel_t fn;
    input=vectors[vec].a;math_b=vectors[vec].b;math_rem=0;
    fn=choose(vectors[vec].op);
    TR0=0;TL0=0;TH0=0;TF0=0;TR0=1;
    for(n=0;n<REPEATS;++n)result=fn(input);
    TR0=0;hi=TH0;lo=TL0;overflow=TF0;
#ifdef __C251__
    /* C251 的循环右移拆字节生成错误；两处统一使用显式移位，只影响计时后的回传。 */
    value=result;
    cache[rec][0]=(u8)value;cache[rec][1]=(u8)(value>>8);
    cache[rec][2]=(u8)(value>>16);cache[rec][3]=(u8)(value>>24);
    value=math_rem;
    cache[rec][4]=(u8)value;cache[rec][5]=(u8)(value>>8);
    cache[rec][6]=(u8)(value>>16);cache[rec][7]=(u8)(value>>24);
#else
    value=result;for(j=0;j<4;++j){cache[rec][j]=(u8)value;value>>=8;}
    value=math_rem;for(j=0;j<4;++j){cache[rec][4+j]=(u8)value;value>>=8;}
#endif
    cache[rec][8]=lo;cache[rec][9]=hi;cache[rec][10]=overflow;cache[rec][11]=REPEATS;
}
static void packet(void)
{
    u16 vec;u8 j,checksum;
    vec=current/SAMPLES;
    if(current==completed && completed<RECORDS){measure(current,vec);++completed;}
    for(j=0;j<64;++j)usb_packet[j]=0;
    usb_packet[0]='M';usb_packet[1]='X';usb_packet[2]='M';usb_packet[3]='1';
    usb_packet[4]=(u8)sequence;usb_packet[5]=(u8)(sequence>>8);
    usb_packet[6]=(u8)current;usb_packet[7]=(u8)(current>>8);
    for(j=0;j<12;++j)usb_packet[8+j]=cache[current][j];
    usb_packet[20]=vectors[vec].original_id;usb_packet[21]=(u8)(current%SAMPLES);usb_packet[22]=vectors[vec].op;
    put32(24,vectors[vec].a);put32(28,vectors[vec].b);
    usb_packet[32]=WTST;usb_packet[33]=CKCON;usb_packet[34]=IRCBAND;usb_packet[35]=TM0PS;
    usb_packet[36]=TEST_VARIANT;usb_packet[37]=AUXR&0x80;usb_packet[38]=TMOD&0x0f;usb_packet[39]=EA;
    usb_packet[53]=usb_fault;usb_packet[54]=2;
    usb_packet[56]=0x27;usb_packet[57]=0x10;usb_packet[58]=0x32;usb_packet[59]=2;
    checksum=0;for(j=0;j<63;++j)checksum^=usb_packet[j];usb_packet[63]=checksum;
    ++sequence;if(++current==RECORDS)current=0;
}
void main(void)
{
    EA=0;WTST=0;CKCON=0;P_SW2|=0x80;
    completed=0;current=0;sequence=0;math_b=0;math_rem=0;
    /* 与原固定数学工程相同：Timer0 /12、预分频0、8次调用、无中断。 */
    TR0=0;AUXR&=0x7f;TMOD=(TMOD&0xf0)|1;TM0PS=0;TF0=0;
    usb_init();
    for(;;){usb_poll();if(usb_ready()){packet();usb_poll();if(usb_ready())usb_send();}}
}
