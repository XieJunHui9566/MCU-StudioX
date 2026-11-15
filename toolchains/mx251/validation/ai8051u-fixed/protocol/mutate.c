#include "app.h"
void mutate_word(u32 PTR *value,u16 salt) API {*value=(*value^0x9e3779b9UL)+(u32)salt;}
