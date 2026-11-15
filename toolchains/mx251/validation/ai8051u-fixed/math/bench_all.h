unsigned long kernel0(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel1(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel2(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel3(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel4(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel5(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel6(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel7(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel8(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel9(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel10(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel11(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel12(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel13(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel14(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel15(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel16(unsigned long,unsigned long) __reentrant __sdcccall(4);
unsigned long kernel17(unsigned long,unsigned long) __reentrant __sdcccall(4);

typedef unsigned long (*callback_t)(unsigned long,unsigned long) __reentrant __sdcccall(4);
static unsigned long invoke(unsigned char k,unsigned long a,unsigned long b) {
switch(k) {
case 0: return kernel0(a,b);
case 1: return kernel1(a,b);
case 2: return kernel2(a,b);
case 3: return kernel3(a,b);
case 4: return kernel4(a,b);
case 5: return kernel5(a,b);
case 6: return kernel6(a,b);
case 7: return kernel7(a,b);
case 8: return kernel8(a,b);
case 9: return kernel9(a,b);
case 10: return kernel10(a,b);
case 11: return kernel11(a,b);
case 12: return kernel12(a,b);
case 13: return kernel13(a,b);
case 14: return kernel14(a,b);
case 15: return kernel15(a,b);
case 16: return kernel16(a,b);
case 17: return kernel17(a,b);

default:return kernel0(a,b);
}}
