#include "system_config.h"

int main(void)
{
    System_Init();

    for (;;) {
        /* 在这里编写应用代码。 */
        System_Delay(1);
    }
}
