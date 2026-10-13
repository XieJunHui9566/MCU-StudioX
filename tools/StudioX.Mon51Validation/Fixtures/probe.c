/* 仅验证标准 8051 指令和调试符号，不访问 STC 专用外设。 */
volatile __data __at (0x30) unsigned char counter;
volatile __xdata __at (0x0020) unsigned int total;

void checkpoint(void)
{
    counter++;
    total += counter;
}

void main(void)
{
    counter = 0;
    total = 0;
    while (1)
    {
        checkpoint();
    }
}
