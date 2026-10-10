typedef struct {
    signed char delta;
    unsigned int word;
    unsigned char samples[3];
} Sample;
volatile __data __at (0x40) signed char signed_value;
volatile __data __at (0x50) unsigned char array[4];
volatile __xdata __at (0x80) Sample sample;
volatile __xdata __at (0x90) float real;
__xdata unsigned char * __data __at (0x60) pointer;
__bit flag;
__sfr __at (0xe0) ACC;
unsigned int work(unsigned char input) __reentrant
{
    volatile unsigned char local = input;
    return local + sample.word;
}
void main(void)
{
    signed_value = -3;
    sample.word = 0x1234;
    pointer = (__xdata unsigned char *)0x80;
    flag = 1;
    while (1) { array[1] = (unsigned char)work(ACC); }
}
