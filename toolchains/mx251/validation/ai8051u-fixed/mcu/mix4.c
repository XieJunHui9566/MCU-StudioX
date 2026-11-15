#include "workloads.h"
/* 独立翻译单元，固定评估真实四参数调用成本。 */
u32 mix4(u32 a,u32 b,u32 c,u32 d) ABI
{
    return ((a<<3)|(a>>29))+(b^d)+c;
}
