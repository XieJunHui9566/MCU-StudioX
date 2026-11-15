#include "common.h"

volatile unsigned char observed;

/* C 区分大小写；不可把死 Start 与活 start 合并为同一个所有者。 */
void dead_bridge(void)
{
    Start();
}

void main(void)
{
    observed = start(observed);
    while (1) { }
}
