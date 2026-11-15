typedef unsigned char u8;typedef unsigned int u16;typedef unsigned long u32;
u32 imul(u32 a,u32 b) __reentrant{return a*b;}
u32 idiv(u32 a,u32 b) __reentrant{return a/b;}
u32 imod(u32 a,u32 b) __reentrant{return a%b;}
long sdiv(long a,long b) __reentrant{return a/b;}
long smod(long a,long b) __reentrant{return a%b;}
