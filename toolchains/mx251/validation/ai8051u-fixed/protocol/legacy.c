#include "app.h"
/* 故意保留旧调用约定和可取址形参，验证跨模块新旧后端互操作。 */
u32 revise_seed(u32 seed,u16 salt) LEGACY {mutate_word(&seed,salt);return seed;}
