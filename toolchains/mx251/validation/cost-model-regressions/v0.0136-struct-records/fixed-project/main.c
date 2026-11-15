typedef unsigned char uint8_t;
typedef unsigned int uint16_t;
typedef unsigned long uint32_t;
#include "workloads.h"
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
u8 XDATA inputs[64],output[64];
static u8 XDATA records[1179][16];static u16 record_count;static u8 checksum;
static const u32 CODE seeds[]={0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000000UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0x00000001UL,0xc1f95874UL,0x18dbf5ecUL,0x55ad7868UL,0x78be739aUL,0xfe9e3f6aUL,0xf9fcf997UL,0x4261f93cUL,0x4a1dd584UL,0xf2b191c2UL,0xf8b84059UL,0x68718d8aUL,0x9959e489UL,0xe36c6718UL,0x4eea30d1UL,0x77696a45UL,0x01656fdfUL,0x39a117c5UL,0x725b7a14UL,0xdb0518caUL,0xb02d9469UL,0xe53aca3fUL,0x8e7e5e59UL,0xc0eaf547UL,0x2636c18dUL,0x62b73498UL,0x6eb8af10UL,0x43c4a93fUL,0xf080a80fUL,0x7026dfc6UL,0x84a19422UL,0xce880584UL,0xa23ca476UL,0x53a4292aUL,0xbe267df6UL,0x1be5439eUL,0xe2f79c5eUL,0xc706d847UL,0x96a7fe5fUL,0x205fab10UL,0x25d60586UL,0xb2d30c65UL,0xd7e68ce8UL,0xc418e6e8UL,0x3a60050bUL,0xa315c4e2UL,0x7b4fbd5bUL,0xd2a9cf16UL,0xed584592UL,0xb261af1bUL,0x5d3d911fUL,0x3bb506ebUL,0xb12da70bUL,0xd5d1b462UL,0x85aa707eUL,0x12693c26UL,0x6db82734UL,0x076dc2daUL,0x69925062UL,0xd2133186UL,0x32557774UL,0x0394730fUL,0xf59e6ffcUL,0x2a5308adUL,0x359bb47bUL,0x31f60a10UL,0x8dfbc142UL,0x83d9acaaUL,0xf612a1c9UL,0xa1688510UL,0xd34a762eUL,0x487ec27cUL,0x10822185UL,0x9337837fUL,0x834156f4UL,0x5f591e3fUL,0x02fbc623UL,0x68a27607UL,0x5abbcd9eUL,0x735d2e82UL,0xd5fb95caUL,0xe1d540bcUL,0xa63b429dUL,0xfb899851UL,0xe3ef22cdUL,0x67321d2cUL,0x6b403639UL,0x671f3dbcUL,0xab290a8aUL,0xb1c0a0d9UL,0x117f55ddUL,0x665cca7aUL,0x6fc0ede9UL,0x4b34a4d2UL,0x1c7d52ddUL,0x9fc25e78UL,0xe7c45011UL};
static const u32 CODE counts[]={0x00000000UL,0x00000001UL,0x00000002UL,0x00000003UL,0x00000004UL,0x00000005UL,0x00000006UL,0x00000007UL,0x00000008UL,0x0000000bUL,0x0000000cUL,0x0000000fUL,0x00000010UL,0x0000001fUL,0x0000003fUL,0x00000040UL,0x00000000UL,0x00000001UL,0x00000002UL,0x00000003UL,0x00000004UL,0x00000005UL,0x00000006UL,0x00000007UL,0x00000008UL,0x0000000bUL,0x0000000cUL,0x0000000fUL,0x00000010UL,0x0000001fUL,0x0000003fUL,0x00000040UL,0x0000003fUL,0x00000010UL,0x00000001UL,0x00000002UL,0x000000ffUL,0x00000010UL,0x00000041UL,0x00000003UL,0x000000ffUL,0x00000008UL,0x0000003fUL,0x00000010UL,0x0000000fUL,0x00000007UL,0x00000007UL,0x00000006UL,0x0000000fUL,0x00000008UL,0x0000001fUL,0x00000007UL,0x0000000fUL,0x00000007UL,0x0000003fUL,0x00000003UL,0x00000006UL,0x0000000cUL,0x0000001fUL,0x00000010UL,0x00000002UL,0x00000004UL,0x00000008UL,0x0000003fUL,0x00000010UL,0x00000006UL,0x0000000bUL,0x00000001UL,0x00000041UL,0x0000000fUL,0x00000007UL,0x0000003fUL,0x00000010UL,0x000000ffUL,0x0000000fUL,0x00000040UL,0x00000007UL,0x0000000fUL,0x00000008UL,0x00000041UL,0x00000002UL,0x0000001fUL,0xffffffffUL,0x00000003UL,0x0000000fUL,0x0000003fUL,0x00000000UL,0x00000008UL,0x00000003UL,0x00000004UL,0x00000002UL,0x00000010UL,0x00000008UL,0x00000000UL,0x0000000fUL,0x00000002UL,0x00000010UL,0x00000040UL,0xffffffffUL,0x00000010UL,0x0000001fUL,0x000000ffUL,0x00000007UL,0xffffffffUL,0x00000004UL,0x00000006UL,0x00000002UL,0x00000007UL,0x00000005UL,0x0000000fUL,0x00000041UL,0x00000006UL,0x00000001UL,0x00000001UL,0x00000002UL,0x00000040UL,0x00000003UL,0x00000010UL,0x00000003UL,0x00000005UL,0x0000000fUL,0x00000041UL,0x00000004UL,0x00000002UL,0x000000ffUL,0x0000000bUL,0x00000000UL,0x00000004UL};
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

static void prepare(u32 seed){u8 i;for(i=0;i<64U;++i){inputs[i]=(u8)((u16)i*37U+(i>>1)+(u8)seed);output[i]=0xccU;}
inputs[0]=0xa5;inputs[1]=3;inputs[2]=0x11;inputs[3]=0x22;inputs[4]=0x33;inputs[5]=3;inputs[6]=0xa5;inputs[7]=0;inputs[8]=0xa5;inputs[9]=1;inputs[10]=0x5a;inputs[11]=0;}
static u32 invoke(u8 k,u32 seed,u32 count){switch(k){
case 0:return identity(seed,count);
case 1:return sum_bytes(seed,count);
case 2:return copy_xor(seed,count);
case 3:return ring_buffer(seed,count);
case 4:return state_machine(seed,count);
case 5:return frame_parser(seed,count);
case 6:return struct_records(seed,count);
case 7:return crc16(seed,count);
case 8:return call_chain(seed,count);
default:return 0;}}
static u32 check_output(u8 k,u32 seed,u32 count){u8 i,n=(u8)(count>64UL?64UL:count),wanted;u32 errors=0;
for(i=0;i<64U;++i){wanted=(k==2U && i<n)?(u8)(inputs[i]^(u8)seed):0xccU;if(output[i]!=wanted)++errors;}return errors;}
static void save(u16 id,u8 sample,u8 repeats,u16 ticks,u8 overflow,u8 status,u32 result,u32 aux){u8 j,*p;u16 ix=record_count++;
records[ix][0]=(u8)id;records[ix][1]=(u8)(id>>8)*2+1;records[ix][2]=sample;records[ix][3]=repeats;
records[ix][4]=(u8)(ticks>>8);records[ix][5]=(u8)ticks;records[ix][6]=overflow;records[ix][7]=status;
p=(u8*)&result;for(j=0;j<4;++j)records[ix][8+j]=p[j];p=(u8*)&aux;for(j=0;j<4;++j)records[ix][12+j]=p[j];}
void main(void){u16 c,ix,ticks;u8 k,s,j,rep,overflow;u32 seed,count,result,aux;init();record_count=0;
for(c=0;c<128U;++c){seed=seeds[c];count=counts[c];for(k=0;k<9U;++k){prepare(seed);result=invoke(k,seed,count);aux=check_output(k,seed,count);save(c*9U+k,0,1,0,0,58,result,aux);}}
for(k=0;k<9U;++k)for(s=0;s<3U;++s){seed=0x12345678UL;count=64UL;prepare(seed);TL0=0;TH0=0;TF0=0;TR0=1;
for(rep=0;rep<8U;++rep)result=invoke(k,seed,count);TR0=0;ticks=((u16)TH0<<8)|TL0;overflow=TF0;
aux=check_output(k,seed,count);save(1152U+k,s,8,ticks,overflow,59,result,aux);}
for(;;)for(ix=0;ix<record_count;++ix){checksum=0;send('M');send('9');for(j=0;j<16U;++j)send(records[ix][j]);send(checksum);send(13);send(10);}}
