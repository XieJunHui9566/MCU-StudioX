/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "instruction_effects.h"
#include <assert.h>
#include <stdio.h>
static mx251_effects effect(const char *text){return mx251TextEffects(text,strlen(text));}
static void top(const char *text){mx251_effects e=effect(text);assert(e.unknown && e.writes==UINT32_MAX && e.specialWrites==UINT32_MAX && e.flagsWrites==UINT32_MAX);}
int main(int argc,char **argv) {
  if(argc==2) {
    FILE *f=fopen(argv[1],"rb");assert(f);assert(!fseek(f,0,SEEK_END));long n=ftell(f);assert(n>=0 && n<1048576);rewind(f);
    char *text=malloc((size_t)n+1);assert(text);assert(fread(text,1,(size_t)n,f)==(size_t)n);fclose(f);text[n]=0;
    mx251_effects e=mx251TextEffects(text,(size_t)n);free(text);
    printf("{\"reads\":\"%08lx\",\"writes\":\"%08lx\",\"specialReads\":\"%08lx\",\"specialWrites\":\"%08lx\",\"flagsReads\":\"%08lx\",\"flagsWrites\":\"%08lx\",\"memory\":\"%u\",\"instructions\":\"%u\",\"unknown\":\"%u\",\"calls\":\"%u\"}\n",
      (unsigned long)e.reads,(unsigned long)e.writes,(unsigned long)e.specialReads,(unsigned long)e.specialWrites,
      (unsigned long)e.flagsReads,(unsigned long)e.flagsWrites,e.memory,e.instructions,e.unknown,e.calls);return 0;
  }
  mx251_effects e=effect("\tmov a,r0\n");assert(!e.unknown && e.reads==1 && e.writes==0x800 && e.flagsWrites==UINT32_MAX);
  e=effect("MOV WR10,WR2\n");assert(!e.unknown && e.reads==0xc && e.writes==0xc00);
  e=effect("mov dr8,dr24\n");assert(!e.unknown && e.reads==0x0f000000 && e.writes==0xf00);
  e=effect("mov dpl,a\nmov wr18,wr58\n");assert(!e.unknown && e.specialWrites==1 && e.specialReads==1 && e.writes==0xc0000);
  e=effect("mov dpx,dr16\nmov spx,dr20\n");assert(!e.unknown && e.specialWrites==7);
  e=effect("push dr24\npop dr24\neret\n");assert(!e.unknown && e.writes==0x0f000000 && e.reads==e.writes && e.memory==3 && e.specialWrites==4);
  e=effect("mov r0,@spx-0x10\nmov @spx+2,wr4\n");assert(!e.unknown && e.writes==1 && e.reads==0x30 && e.memory==3 && e.specialReads==4);
  e=effect("cmp dr4,#0\nje __wm_Test_1\nmov dr4,#1\n__wm_Test_1:\neret\n");assert(!e.unknown && e.writes==0xf0 && e.flagsReads==UINT32_MAX);
  e=effect("jz __wm_Test_1\n__wm_Test_1:\nnop\n");assert(!e.unknown && e.reads==0x800);
  e=effect("; unknown comment\n_Test:\n\n");assert(!e.unknown && !e.instructions && !e.writes);
  const char *unknown[]={"mul wr4,wr6\n","div dr4,wr8\n","mov r16,#1\n","mov wr3,#1\n","mov dr30,#1\n","mov dr32,#1\n","mov a,P0\n","mov @dr4,r0\n","mov r0,@dr4\n","mov @spx+symbol,r0\n","mov @spx+,r0\n","mov #1,r0\n","mov r0,r1,r2\n","ret x\n","sjmp _outside\n","sjmp __wm_absent\n","ejmp @dr16\n","sjmp __wm_test_1\n__wm_Test_1:\n","reti\n",".db 0\n","label: mov r0,#1\n","nop garbage\n","\tmov r0,\n"};
  for(unsigned i=0;i<sizeof unknown/sizeof *unknown;++i)top(unknown[i]);
  e=effect("ecall _known\n");assert(e.unknown && e.calls==1);top("ecall _known\nmov r0,#0\n");
  char large[400];memset(large,'a',sizeof large);large[sizeof large-1]=0;top(large);
  /* 包含每个编码寄存器及其物理重叠，不能按名称把 WR/DR 当作独立资源。 */
  for(unsigned w=1;w<=4;w*=2)for(unsigned i=0;i<(w==1?16:32);i+=w){char line[64];sprintf(line,"mov %sr%u,#0\n",w==1?"":w==2?"w":"d",i);e=effect(line);assert(!e.unknown && e.writes==(((UINT32_C(1)<<w)-1)<<i));}
  puts("instruction effects: aliases, stack, branches, unknown-top and merge checks passed");return 0;
}
