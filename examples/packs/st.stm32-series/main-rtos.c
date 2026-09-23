#include "system_config.h"

int main(void)
{
    System_Init();

    /* 在这里初始化外设并创建应用任务。任务函数放在自己的 .c 文件中。 */
    System_Start();
}
