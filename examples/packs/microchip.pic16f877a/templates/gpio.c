#include <xc.h>

/* 仅适用于已确认的外部 4 MHz 晶振；RD0 是否接 LED 需要核对原理图。 */
#define _XTAL_FREQ 4000000UL
#pragma config FOSC = XT
#pragma config WDTE = OFF
#pragma config PWRTE = ON
#pragma config BOREN = ON
#pragma config LVP = OFF
#pragma config CPD = OFF
#pragma config WRT = OFF
#pragma config CP = OFF

void main(void)
{
    PORTD = 0;
    TRISDbits.TRISD0 = 0;
    for (;;)
    {
        PORTDbits.RD0 = !PORTDbits.RD0;
        __delay_ms(250);
    }
}
