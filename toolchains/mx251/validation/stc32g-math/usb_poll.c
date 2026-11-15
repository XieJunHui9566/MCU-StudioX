/* Isolated board acceptance transport, not a general USB device package.
 * Register sequencing: STC32G manual and official USB reference (source lock nearby).
 * No IAP, DFU, reset or bootloader command is accepted by this firmware.
 */
#include "stc32g_test.h"
__xdata u8 usb_packet[64];
u8 usb_fault;
static u8 configured, idle_rate;
static __xdata u8 setup[8];
static __xdata u8 response[64];
/* The vendor example's identity is used only on this private test sample. */
static const __code u8 dev_desc[]={18,1,0,2,0,0,0,64,0xbf,0x34,1,0xff,1,1,1,2,0,1};
static const __code u8 cfg_desc[]={9,2,41,0,1,1,0,0x80,50,9,4,0,0,2,3,0,0,0,9,0x21,1,1,0,1,0x22,27,0,7,5,0x81,3,64,0,10,7,5,2,3,64,0,10};
static const __code u8 hid_desc[]={5,12,9,1,0xa1,1,0x15,0,0x25,0xff,0x75,8,0x95,64,9,1,0x81,2,9,1,0x91,2,9,1,0xb1,2,0xc0};
static const __code u8 lang[]={4,3,9,4};
static const __code u8 manufacturer[]={14,3,'M',0,'X',0,'2',0,'5',0,'1',0,' ',0};
static const __code u8 product[]={28,3,'M',0,'X',0,'2',0,'5',0,'1',0,' ',0,'S',0,'T',0,'C',0,'3',0,'2',0,'G',0,' ',0};

static u8 wait_reg(void)
{
    u16 left=2000;
    while(USBADR&0x80) if(!--left){usb_fault=1;return 0;}
    return 1;
}
static u8 rd(u8 reg)
{
    if(!wait_reg())return 0;
    USBADR=reg|0x80;
    if(!wait_reg())return 0;
    return USBDAT;
}
static void wr(u8 reg,u8 value)
{
    if(!wait_reg())return;
    USBADR=reg&0x7f;USBDAT=value;
}
static void stall(void){wr(CSR0,0x60);}
static void status(void){wr(CSR0,0x48);}
static void copy_desc(const __code u8 *p,u8 count)
{
    u8 i;
    for(i=0;i<count;i++)response[i]=p[i];
}
static void control(void)
{
    u8 i, count, request, kind, value, csr;
    u16 length;
    wr(INDEX,0);csr=rd(CSR0);
    if(csr&0x04)wr(CSR0,0);
    if(csr&0x10){wr(CSR0,0x80);}

    if(!(csr&1))return;
    count=rd(COUNT0);
    if(count!=8){
        for(i=0;i<count;i++)rd(FIFO0);
        if(count)stall();else wr(CSR0,0x40);
        return;
    }
    for(i=0;i<8;i++)setup[i]=rd(FIFO0);
    length=(u16)setup[6]|((u16)setup[7]<<8);
    request=setup[1];kind=setup[0];value=setup[2];count=0;
    if(request==6 && (kind==0x80 || kind==0x81)){
        switch(setup[3]){
        case 1: if(value){stall();return;} count=18;copy_desc(dev_desc,count);break;
        case 2: if(value){stall();return;} count=41;copy_desc(cfg_desc,count);break;
        case 3:
            if(value==0){count=4;copy_desc(lang,count);}
            else if(value==1){count=14;copy_desc(manufacturer,count);}
            else if(value==2){count=28;copy_desc(product,count);}
            else {stall();return;} break;
        case 0x21:count=9;copy_desc(cfg_desc+18,count);break;
        case 0x22:count=27;copy_desc(hid_desc,count);break;
        default:stall();return;
        }
    }else if(request==5 && kind==0 && !setup[3] && value<128 && !length && !setup[4] && !setup[5]){
        wr(FADDR,value);status();return;
    }else if(request==9 && kind==0 && value<=1 && !setup[3] && !length){
        configured=value;
        wr(INDEX,1);wr(INCSR2,0x20);wr(INMAXP,8);wr(INCSR1,0x48);
        wr(INDEX,2);wr(INCSR2,0);wr(OUTMAXP,8);wr(OUTCSR1,0x90);
        wr(INDEX,0);status();return;
    }else if(request==8 && kind==0x80 && length==1){response[0]=configured;count=1;
    }else if(request==0 && (kind==0x80 || kind==0x81 || kind==0x82) && length==2){response[0]=0;response[1]=0;count=2;
    }else if(request==1 && kind==2 && !length && !value && !setup[3] && (setup[4]==2 || setup[4]==0x81)){
        wr(INDEX,setup[4]&0x0f);if(setup[4]&0x80)wr(INCSR1,0x48);else wr(OUTCSR1,0x90);wr(INDEX,0);status();return;
    }else if(request==10 && kind==0x81 && length==1){response[0]=0;count=1;
    }else if(request==11 && kind==1 && !value && !length){status();return;
    }else if(request==10 && kind==0x21 && !length && !value){idle_rate=setup[3];status();return;
    }else if(request==2 && kind==0xa1 && length==1){response[0]=idle_rate;count=1;
    }else if(request==1 && kind==0xa1 && length<=64){for(i=0;i<64;i++)response[i]=usb_packet[i];count=64;
    }else{stall();return;}
    if(length<count)count=(u8)length;
    wr(CSR0,0x40);
    for(i=0;i<count;i++)wr(FIFO0,response[i]);
    wr(CSR0,0x0a);
}
void usb_init(void)
{
    u16 left=65535;
    configured=0;idle_rate=0;usb_fault=0;
    P_SW2|=0x80;P3M0&=0xfc;P3M1|=3;
    IRC48MCR=0x80;
    while(!(IRC48MCR&1))if(!--left){usb_fault=2;return;}
    /* 手册 7.5.1：选择 IRC48M，同时保留 CPU IRC 频段。 */
    IRCBAND=(IRCBAND&0x3f)|0x80;
    USBCLK=0;USBCON=0x90;
    wr(FADDR,0);wr(POWER,9);wr(INTRIN1E,3);wr(INTROUT1E,4);wr(INTRUSBE,7);wr(POWER,1);
}
void usb_poll(void)
{
    u8 events,in_events,out_events,count,i;
    if(usb_fault)return;
    events=rd(INTRUSB);in_events=rd(INTRIN1);out_events=rd(INTROUT1);
    if(events&4){
        configured=0;wr(FADDR,0);
        wr(INDEX,1);wr(INCSR1,0x48);wr(INDEX,2);wr(OUTCSR1,0x90);wr(INDEX,0);
    }
    if(in_events&1)control();
    /* Output reports are drained, never interpreted as commands. */
    if(out_events&4){wr(INDEX,2);count=rd(COUNT0);for(i=0;i<count;i++)rd(FIFO2);wr(OUTCSR1,0);}
}
u8 usb_ready(void)
{
    if(!configured || usb_fault)return 0;
    wr(INDEX,1);return !(rd(INCSR1)&1);
}
void usb_send(void)
{
    u8 i;
    wr(INDEX,1);
    for(i=0;i<64;i++)wr(FIFO1,usb_packet[i]);
    wr(INCSR1,1);
}
