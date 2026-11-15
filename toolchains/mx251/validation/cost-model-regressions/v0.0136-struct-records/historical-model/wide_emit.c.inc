/* SPDX-License-Identifier: GPL-2.0-or-later
 * 宽 MIR 的独立发射器。仅使用已验证的寄存器位置，不调用旧字节发射器。 */
#include "wide_branch.c.inc"
static void we (wide_function *f,const char *format,...) {
  size_t begin=dbuf_get_length(&f->output);
  va_list ap;va_start(ap,format);dbuf_vprintf(&f->output,format,ap);va_end(ap);
  const char *p=dbuf_get_buf(&f->output);size_t end=dbuf_get_length(&f->output);
  for(size_t i=begin;i<end;++i)if(p[i]=='\t' && (!i || p[i-1]=='\n')) {++f->emitInstructions;f->emitCost+=f->emitWeight;}
  for(size_t i=begin;i<end;) {
    size_t next=i;while(next<end && p[next]!='\n')++next;
    if(next==end)f->relaxDisabled=true;
    if(p[i]=='\t') {unsigned bound=wbranch_instruction_bound(p+i+1,next-i-1);if(bound)f->emitUpper+=bound;else ++f->emitBarrier;}
    else if(next>i && p[i]!=';' && p[next-1]!=':')++f->emitBarrier;
    i=next+1;
  }
}
static const char *wp(int width) {return width==4?"dr":"wr";}
static void wl(wide_function *f,int label) {
  if(f->labelCount<WN_MAX*4)f->labels[f->labelCount++]=(wide_label_site){label,dbuf_get_length(&f->output),f->emitUpper,f->emitBarrier};
  else f->relaxDisabled=true;
  we(f,"__wm_%s_%d:\n",currFunc->rname,label);
}
static void wj(wide_function *f,const char *op,int label) {we(f,"\t%s\t__wm_%s_%d\n",op,currFunc->rname,label);}
static int wt(wide_function *f) {return 1000000+f->serial++;}
static void wa(char *buf,int displacement) {
  if(!displacement) strcpy(buf,"@spx");
  else SNPRINTF(buf,32,"@spx%c0x%04x",displacement<0?'-':'+',abs(displacement));
}
static void wi(wide_function *f,int reg,int width,uint32_t value) {
  if(width==2)value&=0xffff;
  if(!value) we(f,"\tsub\t%s%d,%s%d\n",wp(width),reg,wp(width),reg);
  else if(width==2 || value<=0xffff) we(f,"\tmov\t%s%d,#0x%04lx\n",wp(width),reg,(unsigned long)value);
  else {we(f,"\tmov\twr%d,#0x%04lx\n",reg,(unsigned long)(value>>16));we(f,"\tmov\twr%d,#0x%04lx\n",reg+2,(unsigned long)(value&0xffff));}
}
static void wsign_word(wide_function *f,int high,int low) {
  int skip=wt(f);wi(f,high,2,0);we(f,"\tcmp\twr%d,#0x8000\n",low);wj(f,"jc",skip);wi(f,high,2,0xffff);wl(f,skip);
}
static void
wload(wide_function *f,wide_operand o,int dest,int width)
{
  if(o.value<0) {
    uint32_t v=o.literal;
    if(o.sign && o.width<width && (v&(UINT32_C(1)<<(8*o.width-1)))) v|=~((UINT32_C(1)<<(8*o.width))-1);
    wi(f,dest,width,v);return;
  }
  wide_value *v=&f->values[o.value];
  if(o.width>=width) {
    int offset=o.width-width;
    if(v->reg>=0) {if(dest!=v->reg+offset) we(f,"\tmov\t%s%d,%s%d\n",wp(width),dest,wp(width),v->reg+offset);}
    else {
      for(int k=0;k<width;k+=2) {char address[32];wa(address,v->slot+offset+k-f->frame-f->stackDepth+1);we(f,"\tmov\twr%d,%s\n",dest+k,address);}
    }
  } else {
    int low=dest+width-2;
    if(o.width==1) {
      if(v->reg>=0) we(f,"\t%s\twr%d,r%d\n",o.sign?"movs":"movz",low,v->reg);
      else {char address[32];wa(address,v->slot-f->frame-f->stackDepth+1);we(f,"\tmov\ta,%s\n\t%s\twr%d,a\n",address,o.sign?"movs":"movz",low);}
    } else {
      if(v->reg>=0) {if(low!=v->reg) we(f,"\tmov\twr%d,wr%d\n",low,v->reg);}
      else {char address[32];wa(address,v->slot-f->frame-f->stackDepth+1);we(f,"\tmov\twr%d,%s\n",low,address);}
    }
    if(width==4) {if(o.sign) wsign_word(f,dest,low);else wi(f,dest,2,0);}
  }
}
static void
wstore_byte(wide_function *f,int value,int source)
{
  wide_value *v=&f->values[value];wassert(v->width==1 && source>=0 && source<16);
  /* 只写一字节；溢出槽仍经保留 A 访问，不触及相邻槽或活跃字节。 */
  if(v->reg>=0) {if(v->reg!=source)we(f,"\tmov\tr%d,r%d\n",v->reg,source);}
  else {char address[32];wa(address,v->slot-f->frame-f->stackDepth+1);
    if(source!=11)we(f,"\tmov\ta,r%d\n",source);we(f,"\tmov\t%s,a\n",address);}
}
static void
wstore_known(wide_function *f,int value,int source,bool pointerClean)
{
  wide_value *v=&f->values[value];
  /* MIR 用四字节保存远指针，ABI/内存表示仍是三字节；每次定义清除填充位。 */
  if(v->pointer && !pointerClean)we(f,"\tanl\twr%d,#0x00ff\n",source);
  if(v->width==1) {
    /* WR 的低字节为 R(source+1)；高组没有字节别名，仍先快照到 B:A。 */
    if(source<16)wstore_byte(f,value,source+1);
    else {we(f,"\tmov\twr10,wr%d\n",source);wstore_byte(f,value,11);}
  } else if(v->reg>=0) {
    if(v->reg!=source) we(f,"\tmov\t%s%d,%s%d\n",wp(v->width),v->reg,wp(v->width),source);
  } else for(int k=0;k<v->width;k+=2) {
    char address[32];wa(address,v->slot+k-f->frame-f->stackDepth+1);we(f,"\tmov\t%s,wr%d\n",address,source+k);
  }
}
static void wstore(wide_function *f,int value,int source) {wstore_known(f,value,source,false);}
static bool
wcanonical_pointer(wide_function *f,wide_operand o)
{
  /* 所有 MIR 指针定义及入口已规范化；不能把任意 32 位整数当作指针。 */
  if(o.value>=0)return f->values[o.value].pointer || (o.width<4 && !o.sign);
  uint32_t v=o.literal;
  if(o.sign && o.width<4 && (v&(UINT32_C(1)<<(8*o.width-1))))v|=~((UINT32_C(1)<<(8*o.width))-1);
  return !(v&UINT32_C(0xff000000));
}
static int wread(wide_function *f,wide_operand o,int scratch,int width) {
  if(o.value>=0 && o.width>=width && f->values[o.value].reg>=0) return f->values[o.value].reg+o.width-width;
  wload(f,o,scratch,width);return scratch;
}
/* 字节值只分配到 R0..R15，且排除保留的 B:R10/A:R11。
 * 溢出读取仅借用 A；先装右值到 B，再装左值，可同时处理两个 spill。
 * 不扩展到 WR，也不改变内存访问宽度或调度。 */
static void
wload_byte(wide_function *f,wide_operand o,int dest)
{
  wassert(o.width==1 && dest>=0 && dest<16);
  if(o.value<0)we(f,"\tmov\tr%d,#0x%02lx\n",dest,(unsigned long)(o.literal&255));
  else {
    wide_value *v=&f->values[o.value];
    if(v->reg>=0) {if(v->reg!=dest)we(f,"\tmov\tr%d,r%d\n",dest,v->reg);}
    else {char address[32];wa(address,v->slot-f->frame-f->stackDepth+1);
      we(f,"\tmov\ta,%s\n",address);if(dest!=11)we(f,"\tmov\tr%d,a\n",dest);}
  }
}
static void
wcompare_byte(wide_function *f,wide_operand left,wide_operand right)
{
  int rhs=-1,lhs=11;
  if(right.value>=0) {
    rhs=f->values[right.value].reg;
    if(rhs<0) {wload_byte(f,right,10);rhs=10;}
  }
  if(left.value>=0 && f->values[left.value].reg>=0)lhs=f->values[left.value].reg;
  else wload_byte(f,left,11);
  we(f,"; wide byte compare signed=%d\n",left.sign);
  if(rhs<0)we(f,"\tcmp\tr%d,#0x%02lx\n",lhs,(unsigned long)(right.literal&255));
  else we(f,"\tcmp\tr%d,r%d\n",lhs,rhs);
}
static void
wbitwise_byte(wide_function *f,wide_node *n)
{
  wide_value *dest=&f->values[n->result];wide_operand left=n->left,right=n->right;
  int work=dest->reg>=0?dest->reg:11,rhs=-1;
  /* 三种操作均交换律成立；先交换重叠右值，避免写结果毁掉未读输入。 */
  if(right.value>=0 && f->values[right.value].reg==work) {wide_operand t=left;left=right;right=t;}
  if(right.value>=0) {
    rhs=f->values[right.value].reg;
    if(rhs<0) {wload_byte(f,right,10);rhs=10;}
  }
  wload_byte(f,left,work);
  const char *op=n->op=='^'?"xrl":n->op=='|'?"orl":"anl";
  we(f,"; wide byte bitwise op=%s\n",op);
  if(rhs<0)we(f,"\t%s\tr%d,#0x%02lx\n",op,work,(unsigned long)(right.literal&255));
  else we(f,"\t%s\tr%d,r%d\n",op,work,rhs);
  if(dest->reg<0) {char address[32];wa(address,dest->slot-f->frame-f->stackDepth+1);we(f,"\tmov\t%s,a\n",address);}
}
static void
wbranch(wide_function *f,const char *condition,int target)
{
  /* 先生成距离无关的保底形式，完整发射后再用保守上界证明可缩短的分支。 */
  static const char *pairs[][2]={{"je","jne"},{"jc","jnc"},{"jg","jle"},{"jsg","jsle"},{"jsl","jsge"}};
  const char *inverse=NULL;
  for(int i=0;i<5;++i)for(int j=0;j<2;++j)if(!strcmp(condition,pairs[i][j]))inverse=pairs[i][1-j];
  wassert(inverse);
  wide_branch_site site={target,condition,dbuf_get_length(&f->output),0,f->emitUpper,0,f->emitBarrier,f->emitWeight};
  int skip=wt(f);wj(f,inverse,skip);wj(f,"ejmp",target);wl(f,skip);
  site.end=dbuf_get_length(&f->output);site.upperEnd=f->emitUpper;
  if(f->branchCount<WN_MAX)f->branches[f->branchCount++]=site;else f->relaxDisabled=true;
}
static const char *wcondition(unsigned op,bool sign,bool truth) {
  switch(op) {
  case EQ_OP:return truth?"je":"jne";
  case NE_OP:return truth?"jne":"je";
  case '<':return sign?(truth?"jsl":"jsge"):(truth?"jc":"jnc");
  case GE_OP:return sign?(truth?"jsge":"jsl"):(truth?"jnc":"jc");
  case '>':return sign?(truth?"jsg":"jsle"):(truth?"jg":"jle");
  case LE_OP:return sign?(truth?"jsle":"jsg"):(truth?"jle":"jg");
  default:wassert(false);return NULL;
  }
}
static bool wcompare_op(unsigned op) {return op=='<'||op=='>'||op==LE_OP||op==GE_OP||op==EQ_OP||op==NE_OP;}

static void
wtest_zero(wide_function *f,wide_operand input)
{
  if(input.width==1) {
    int reg=input.value>=0?f->values[input.value].reg:-1;
    if(reg<0) {reg=11;wload_byte(f,input,reg);}
    we(f,"; wide byte truth\n\tcmp\tr%d,#0\n",reg);
  } else {int reg=wread(f,input,16,input.width);we(f,"\tcmp\t%s%d,#0\n",wp(input.width),reg);}
}
static void
wboolean_result(wide_function *f,wide_node *n,const char *condition)
{
  int yes=wt(f),end=wt(f);wide_value *v=&f->values[n->result];
  /* 先消费比较标志再写结果，允许结果复用已死输入；不依赖 MOV 的标志副作用。 */
  wj(f,condition,yes);
  if(v->width==1) {
    int work=v->reg>=0?v->reg:11;
    we(f,"; wide byte boolean\n\tmov\tr%d,#0\n",work);wj(f,"sjmp",end);wl(f,yes);
    we(f,"\tmov\tr%d,#1\n",work);wl(f,end);wstore_byte(f,n->result,work);
  } else {
    int width=n->width==4?4:2;wi(f,16,width,0);wj(f,"sjmp",end);wl(f,yes);
    wi(f,16,width,1);wl(f,end);wstore(f,n->result,16);
  }
}

/* 三字节对象只能触及 [base,base+2]；DR20 的最高补位不落内存。
 * 先快照地址和输入，再写结果，允许别名与寄存器重叠。
 * volatile 严格执行三个按地址升序的字节事务。 */
static void
wmemory_pointer(wide_function *f,wide_node *n)
{
  bool store=n->op==WM_STORE;char a[3][32];
  wassert(n->pointerAccess && n->width==4 && !n->codeAccess && !n->addressOp);
  if(n->addressObject>=0) {
    int base=f->objects[n->addressObject].slot+n->addressOffset-f->frame-f->stackDepth+1;
    for(int k=0;k<3;++k)wa(a[k],base+k);
  } else {
    wload(f,n->left,16,4);
    for(int k=0;k<3;++k)SNPRINTF(a[k],sizeof a[k],k?"@dr16+%d":"@dr16",k);
  }
  we(f,"; wide pointer object %s physical=3 logical=4 volatile-bytes=%d\n",store?"store":"load",n->byteAccess);
  if(store) {
    wload(f,n->right,20,4);
    we(f,"\tmov\twr10,wr20\n\tmov\t%s,a\n",a[0]);
    if(n->byteAccess)we(f,"\tmov\twr10,wr22\n\tmov\ta,b\n\tmov\t%s,a\n\tmov\twr10,wr22\n\tmov\t%s,a\n",a[1],a[2]);
    else we(f,"\tmov\t%s,wr22\n",a[1]);
  } else {
    we(f,"\tmov\ta,%s\n\tmovz\twr20,a\n",a[0]);
    if(n->byteAccess)we(f,"\tmov\ta,%s\n\tmov\tb,a\n\tmov\ta,%s\n\tmov\twr22,wr10\n",a[1],a[2]);
    else we(f,"\tmov\twr22,%s\n",a[1]);
    wstore_known(f,n->result,20,true);
  }
}

static void
wmemory_stack(wide_function *f,wide_node *n)
{
  if(n->pointerAccess){wmemory_pointer(f,n);return;}
  bool store=n->op==WM_STORE;
  int base=f->objects[n->addressObject].slot+n->addressOffset-f->frame-f->stackDepth+1;
  we(f,"; wide memory %s width=%d volatile-bytes=%d\n; wide stack direct object=%d offset=%d\n",
     store?"store":"load",n->width,n->byteAccess,n->addressObject,n->addressOffset);
  if(n->byteAccess || n->width==1) {
    /* 栈地址只依赖 SPX；值位于可逐字节访问的低组寄存器时，无须先
     * 复制到 DR20 再拆字。保留地址升序及原字节事务数，包括 volatile。
     * 指令直接连接低组寄存器和 SPX 相对地址；A/B 重叠仍保守回退。高组、
     * 常量、spill 和三字节指针继续使用已有实现。 */
    int value=store?n->right.value:n->result;
    int reg=value>=0?f->values[value].reg:-1;
    if(reg>=0 && reg+n->width<=16 &&
       !(wide_mask(reg,n->width)&wide_mask(10,2))) {
      we(f,"; wide stack byte direct reg=%d width=%d\n",reg,n->width);
      for(int k=0;k<n->width;++k) {
        char address[32];wa(address,base+k);
        if(store)we(f,"\tmov\t%s,r%d\n",address,reg+k);
        else we(f,"\tmov\tr%d,%s\n",reg+k,address);
      }
      return;
    }
    if(store)wload(f,n->right,20,n->width==1?2:n->width);
    for(int k=0;k<n->width;++k) {
      char address[32];wa(address,base+k);
      if(store) {
        we(f,"\tmov\twr10,wr%d\n",20+(k&~1));
        if(n->width>1 && !(k&1))we(f,"\tmov\ta,b\n");
        we(f,"\tmov\t%s,a\n",address);
      } else {
        we(f,"\tmov\ta,%s\n",address);
        if(n->width==1)we(f,"\tmovz\twr20,a\n");
        else if(!(k&1))we(f,"\tmov\tb,a\n");
        else we(f,"\tmov\twr%d,wr10\n",20+(k&~1));
      }
    }
    if(!store)wstore(f,n->result,20);
  } else {
    int data=20;
    if(store)data=wread(f,n->right,20,n->width);
    else if(f->values[n->result].reg>=0)data=f->values[n->result].reg;
    for(int k=0;k<n->width;k+=2) {
      char address[32];wa(address,base+k);
      if(store)we(f,"\tmov\t%s,wr%d\n",address,data+k);
      else we(f,"\tmov\twr%d,%s\n",data+k,address);
    }
    if(!store)wstore(f,n->result,data);
  }
}

static void
wmemory_symbol(wide_function *f,wide_node *n)
{
  bool store=n->op==WM_SYMBOL_STORE;
  we(f,"; wide symbol %s %s width=%d volatile=%d\n",store?"store":"load",n->addressSymbol->rname,n->width,n->byteAccess);
  int value=store?n->right.value:n->result;
  int reg=value>=0?f->values[value].reg:-1;
  /* 普通直接 RAM 可使用对应宽度的 MOV。volatile/SFR 仍逐字节，
   * 严格保留原事务顺序；只移除 A/B 中转，不缓存对象、不合并访问。 */
  if(!n->byteAccess && n->width>1) {
    if(store) {
      int source=wread(f,n->right,20,n->width);
      we(f,"\tmov\t(%s),%s%d\n",n->addressSymbol->rname,wp(n->width),source);
    } else {
      int dest=reg>=0?reg:20;
      we(f,"\tmov\t%s%d,(%s)\n",wp(n->width),dest,n->addressSymbol->rname);
      wstore(f,n->result,dest);
    }
    return;
  }
  if(store && n->right.value<0) {
    for(int k=0;k<n->width;++k)
      we(f,"\tmov\t(%s+%d),#0x%02lx\n",n->addressSymbol->rname,k,
        (unsigned long)((n->right.literal>>(8*(n->width-k-1)))&255));
    return;
  }
  if(reg>=0 && reg+n->width<=16) {
    for(int k=0;k<n->width;++k) {
      if(store)we(f,"\tmov\t(%s+%d),r%d\n",n->addressSymbol->rname,k,reg+k);
      else we(f,"\tmov\tr%d,(%s+%d)\n",reg+k,n->addressSymbol->rname,k);
    }
    return;
  }
  /* 高组宽寄存器没有可寻址字节别名，溢出槽也必须保留安全取样路径。 */
  if(store)wload(f,n->right,20,n->width==1?2:n->width);
  for(int k=0;k<n->width;++k) {
    if(store) {
      we(f,"\tmov\twr10,wr%d\n",20+(k&~1));
      if(n->width>1 && !(k&1))we(f,"\tmov\ta,b\n");
      we(f,"\tmov\t(%s+%d),a\n",n->addressSymbol->rname,k);
    } else {
      we(f,"\tmov\ta,(%s+%d)\n",n->addressSymbol->rname,k);
      if(n->width==1)we(f,"\tmovz\twr20,a\n");
      else if(!(k&1))we(f,"\tmov\tb,a\n");
      else we(f,"\tmov\twr%d,wr10\n",20+(k&~1));
    }
  }
  if(!store)wstore(f,n->result,20);
}

static void
wmemory(wide_function *f,wide_node *n,bool addressReady)
{
  if(n->pointerAccess){wmemory_pointer(f,n);return;}
  if(n->codeAccess) {
    /* 沿用旧后端 MOVC 的逐字节读取与大端拼装。完整 DPX 自增，
     * 不用 16 位 DPTR 截断跨页地址；A/B 和 WR20/22 均为保留临时寄存器。
     * 先快照地址，最后才写结果，允许输入与输出分配到重叠寄存器。 */
    wassert(n->op==WM_LOAD && n->addressObject<0);
    wload(f,n->left,16,4);we(f,"; wide code load width=%d volatile-bytes=%d\n\tmov\tdpx,dr16\n",n->width,n->byteAccess);
    for(int k=0;k<n->width;++k) {
      we(f,"\tclr\ta\n\tmovc\ta,@a+dptr\n");
      if(n->width==1)we(f,"\tmovz\twr20,a\n");
      else if(!(k&1))we(f,"\tmov\tb,a\n");
      else we(f,"\tmov\twr%d,wr10\n",20+(k&~1));
      if(k+1<n->width)we(f,"\tinc\tdpx\n");
    }
    wstore(f,n->result,20);return;
  }
  if(n->addressObject>=0) {wmemory_stack(f,n);return;}
  bool store=n->op==WM_STORE;
  if(!n->byteAccess) {
    int address;
    if(addressReady)address=16;
    else if(n->addressOp) {
      /* 保留原四字节加减与24位指针规范化，只省去中间地址的存储。
       * DR16/DR20均为保留寄存器；先形成地址，再装载待写入的数据。 */
      we(f,"; wide indexed address op=%c index-width=%d\n",n->addressOp,n->addressIndex.width);
      wload(f,n->left,16,4);
      wide_operand offset=n->addressIndex;uint32_t literal=offset.literal;
      if(offset.sign && offset.width<4 && (literal&(UINT32_C(1)<<(8*offset.width-1))))
        literal|=~((UINT32_C(1)<<(8*offset.width))-1);
      if(offset.value<0 && literal<=0xffff) {
        if(literal)we(f,"\t%s\tdr16,#0x%04lx\n",n->addressOp=='+'?"add":"sub",(unsigned long)literal);
      } else {int index=wread(f,offset,20,4);we(f,"\t%s\tdr16,dr%d\n",n->addressOp=='+'?"add":"sub",index);}
      we(f,"\tanl\twr16,#0x00ff\n");
      address=16;
    } else address=wread(f,n->left,16,4);
    we(f,"; wide memory %s width=%d volatile-bytes=0\n",store?"store":"load",n->width);
    if(n->width==1) {
      if(store) {
        if(n->right.value>=0 && f->values[n->right.value].reg>=0)
          we(f,"\tmov\t@dr%d,r%d\n",address,f->values[n->right.value].reg);
        else {
          if(n->right.value<0)we(f,"\tmov\ta,#0x%02lx\n",(unsigned long)(n->right.literal&255));
          else {char slot[32];wa(slot,f->values[n->right.value].slot-f->frame-f->stackDepth+1);we(f,"\tmov\ta,%s\n",slot);}
          we(f,"\tmov\t@dr%d,a\n",address);
        }
      } else {
        wide_value *v=&f->values[n->result];
        if(v->reg>=0)we(f,"\tmov\tr%d,@dr%d\n",v->reg,address);
        else {char slot[32];wa(slot,v->slot-f->frame-f->stackDepth+1);we(f,"\tmov\ta,@dr%d\n\tmov\t%s,a\n",address,slot);}
      }
      return;
    }
    int data=20;
    if(store)data=wread(f,n->right,20,n->width);
    else {
      wide_value *v=&f->values[n->result];
      /* 多次读同一基址时，先写目的寄存器不能破坏仍需使用的地址。 */
      if(v->reg>=0 && !(wide_mask(v->reg,v->width)&wide_mask(address,4)))data=v->reg;
    }
    for(int k=0;k<n->width;k+=2) {
      char a[32];if(k)SNPRINTF(a,sizeof a,"@dr%d+%d",address,k);else SNPRINTF(a,sizeof a,"@dr%d",address);
      if(store)we(f,"\tmov\t%s,wr%d\n",a,data+k);else we(f,"\tmov\twr%d,%s\n",data+k,a);
    }
    if(!store)wstore(f,n->result,data);
    return;
  }
  if(store)wload(f,n->right,20,n->width==1?2:n->width);
  wload(f,n->left,16,4);
  we(f,"; wide memory %s width=%d volatile-bytes=%d\n",store?"store":"load",n->width,n->byteAccess);
  if(n->width==1) {
    if(store)we(f,"\tmov\twr10,wr20\n\tmov\t@dr16,a\n");
    else we(f,"\tmov\ta,@dr16\n\tmovz\twr20,a\n");
  } else for(int k=0;k<n->width;k+=2) {
    if(n->byteAccess) {
      if(store)we(f,"\tmov\twr10,wr%d\n\tmov\ta,b\n\tmov\t@dr16,a\n\tinc\tdr16\n\tmov\twr10,wr%d\n\tmov\t@dr16,a\n",20+k,20+k);
      else we(f,"\tmov\ta,@dr16\n\tinc\tdr16\n\tmov\tb,a\n\tmov\ta,@dr16\n\tmov\twr%d,wr10\n",20+k);
      if(k+2<n->width)we(f,"\tinc\tdr16\n");
    } else {
      if(store)we(f,"\tmov\t@dr16,wr%d\n",20+k);
      else we(f,"\tmov\twr%d,@dr16\n",20+k);
      if(k+2<n->width)we(f,"\tinc\tdr16,#2\n");
    }
  }
  if(!store)wstore(f,n->result,20);
}


/* 只规划相邻普通存储；WM_NOP 无真实效果，其他节点一律终止。
 * 基址是现有 MIR 契约中的不可取址临时值，不能由 C 对象写入重定义。
 * DR16/DR20 是保留 scratch；数据保持原取样顺序，不跨越读、调用或标签。
 * 每步保留 24 位规范化，不依赖 @DR+disp 的跨顶端回绕语义。 */
static bool
wstore_chain_offset(wide_function *f,wide_node *n,int64_t *offset)
{
  if(n->op!=WM_STORE || (n->addressOp!='+' && n->addressOp!='-') ||
     n->addressObject>=0 || n->byteAccess || n->codeAccess || n->pointerAccess ||
     n->left.value<0 || n->left.width!=4 || n->left.codePointer ||
     (n->width!=1 && n->width!=2 && n->width!=4) ||
     n->right.width!=n->width || n->right.floating ||
     n->addressIndex.value>=0 || n->addressIndex.floating || n->addressIndex.codePointer ||
     (n->addressIndex.width!=1 && n->addressIndex.width!=2 && n->addressIndex.width!=4) ||
     n->result>=0 || n->def.w[0] || n->def.w[1])return false;
  wide_value *base=&f->values[n->left.value];
  if(!base->pointer || base->codePointer || base->symbolSnapshot || base->reg<0 ||
     base->width!=4 || base->sym->addrtaken || isVolatile(base->sym->type) ||
     isAtomic(base->sym->type) ||
     (wide_mask(base->reg,4)&((wide_mask(16,4)|wide_mask(20,4))|MCS251_AB_BYTES)))return false;
  if(n->right.value>=0) {
    wide_value *data=&f->values[n->right.value];
    if(data->width!=n->width || data->pointer ||
       (data->reg>=0 && (wide_mask(data->reg,data->width)&(wide_mask(16,4)|wide_mask(20,4)))))return false;
  }
  unsigned bits=8*n->addressIndex.width;
  uint32_t raw=n->addressIndex.literal;
  if(bits<32)raw&=(UINT32_C(1)<<bits)-1;
  int64_t value=raw;
  if(n->addressIndex.sign && (raw&(UINT32_C(1)<<(bits-1))))
    value-=INT64_C(1)<<bits;
  *offset=n->addressOp=='-'?-value:value;
  return true;
}

static int
wmemory_store_chain(wide_function *f,int first)
{
  wide_node *head=&f->nodes[first];int64_t previous;
  if(!wstore_chain_offset(f,head,&previous))return first;
  int last=first;int64_t initial=previous;
  /* 先验证完整局部计划，再发射；失败节点仍由原路径处理。差值使用
   * int64_t，避免 32 位有符号减法溢出，且只选现有 imm16 ADD/SUB。
   * 原 offset=0 时没有 ADD；不能用一个更长的 ADD 替掉短 MOV。 */
  for(int j=first+1;j<f->nn;++j) {
    wide_node *n=&f->nodes[j];int64_t offset;
    if(n->op==WM_NOP)continue;
    if(!wstore_chain_offset(f,n,&offset) || n->left.value!=head->left.value ||
       n->addressIndex.width!=head->addressIndex.width ||
       n->addressIndex.sign!=head->addressIndex.sign)break;
    int64_t delta=offset-previous;
    if(delta < -65535 || delta > 65535 || (!offset && delta))break;
    last=j;previous=offset;
  }
  if(last==first)return first;
  previous=initial;
  for(int j=first;j<=last;++j) {
    wide_node *n=&f->nodes[j];int64_t offset;
    if(n->op==WM_NOP)continue;
    bool valid=wstore_chain_offset(f,n,&offset);wassert(valid);(void)valid;
    f->emitWeight=1+4*n->origin->depth;
    if(j==first)wmemory(f,n,false);
    else {
      int64_t delta=offset-previous;
      we(f,"; wide store-chain base=v%d previous=%lld offset=%lld delta=%ld\n",n->left.value,(long long)previous,(long long)offset,(long)delta);
      if(delta)we(f,"\t%s\tdr16,#0x%04lx\n",delta<0?"sub":"add",(unsigned long)(delta<0?-delta:delta));
      we(f,"\tanl\twr16,#0x00ff\n");
      wmemory(f,n,true);
    }
    previous=offset;
  }
  /* 地址 ADD/SUB 的 CY/OV 可不同；宽 MIR 不跨存储保留这些标志。
   * 条件使用者重新定义标志，数据访问宽度及实际事务次序没有变化。 */
  return last;
}

static void
wunsigned_multiply(wide_function *f,wide_node *n)
{
  /* DR16/DR20 不参与分配；两个输入先快照到不重叠的 WR16/WR20。
   * MUL 的完整积写 DR16，窄结果取低字 WR18，再由 wstore 取低字节。
   * 新增的 8x8->8 保留两个因子，结果按模 2^8 截断。结果与输入的别名、
   * spill 均交给统一搬运处理，不触及仍活跃的分配寄存器。 */
  wload(f,n->left,16,2);wload(f,n->right,20,2);
  we(f,"; wide unsigned multiply %dx%d -> %d\n\tmul\twr16,wr20\n",
     n->left.width*8,n->right.width*8,n->width*8);
  wstore(f,n->result,n->width==4?16:18);
}
static void
wbinary(wide_function *f,wide_node *n)
{
  if(n->width==1 && n->left.width==1 && n->right.width==1 &&
     (n->op=='^' || n->op=='|' || n->op==BITWISEAND)) {wbitwise_byte(f,n);return;}
  int width=n->width==1?2:n->width;
  wide_value *dest=&f->values[n->result];
  int work=dest->reg>=0&&n->width>1?dest->reg:16;
  wide_operand left=n->left,right=n->right;
  bool commutative=n->op!='-';
  /* 8 位计数器直接使用原生 INC/DEC，仍由宽 MIR 的别名图保证覆盖安全。 */
  if(n->width==1 && dest->reg>=0 && left.value>=0 && left.width==1 &&
     f->values[left.value].reg>=0 && right.value<0 && right.literal==1 &&
     (n->op=='+' || n->op=='-')) {
    int source=f->values[left.value].reg;
    if(source!=dest->reg)we(f,"\tmov\tr%d,r%d\n",dest->reg,source);
    we(f,"\t%s\tr%d\n",n->op=='+'?"inc":"dec",dest->reg);return;
  }
  if(right.value>=0 && f->values[right.value].reg==work && left.value!=right.value) {
    if(commutative){wide_operand t=left;left=right;right=t;}else work=16;
  }
  /* 窄源扩展后，宽结果可能同时覆盖多个输入的字节位置。
   * 必须在写work前保存相交的右值，不能仅比较寄存器起始编号。
   */
  bool leftInPlace=left.value>=0 && left.width>=width &&
    f->values[left.value].reg>=0 && f->values[left.value].reg+left.width-width==work;
  bool rightSnapshot=!leftInPlace && right.value>=0 && f->values[right.value].reg>=0 &&
    (wide_mask(work,width)&wide_mask(f->values[right.value].reg,right.width));
  if(rightSnapshot)wload(f,right,20,width);
  wload(f,left,work,width);
  if(n->op=='+'||n->op=='-') {
    const char *op=n->op=='+'?"add":"sub";
    if(right.value<0 && right.literal<=0xffff) {
      if(right.literal) we(f,"\t%s\t%s%d,#0x%04lx\n",op,wp(width),work,(unsigned long)right.literal);
    } else {int rhs=rightSnapshot?20:wread(f,right,20,width);we(f,"\t%s\t%s%d,%s%d\n",op,wp(width),work,wp(width),rhs);}
  } else {
    const char *op=n->op=='^'?"xrl":n->op=='|'?"orl":"anl";
    int rhs=right.value<0?-1:rightSnapshot?20:wread(f,right,20,width);
    for(int k=0;k<width;k+=2) {
      if(rhs<0) {uint32_t v=(right.literal>>(8*(width-k-2)))&0xffff;
        if((!v && n->op!=BITWISEAND)||(v==0xffff && n->op==BITWISEAND))continue;
        we(f,"\t%s\twr%d,#0x%04lx\n",op,work+k,(unsigned long)v);
      } else we(f,"\t%s\twr%d,wr%d\n",op,work+k,rhs+k);
    }
  }
  wstore(f,n->result,work);
}
static void
wshift_one(wide_function *f,int reg,int width,bool right,bool sign)
{
  if(!right) we(f,"\tadd\t%s%d,%s%d\n",wp(width),reg,wp(width),reg);
  else if(width==2) we(f,"\t%s\twr%d\n",sign?"sra":"srl",reg);
  else {
    int skip=wt(f);we(f,"\tsrl\twr%d\n\t%s\twr%d\n",reg+2,sign?"sra":"srl",reg);
    wj(f,"jnc",skip);we(f,"\torl\twr%d,#0x8000\n",reg+2);wl(f,skip);
  }
}
static void
wshift_byte(wide_function *f,int reg,int width,bool right,bool sign)
{
  /* R0..R15 可直接逐字节搬运，按覆盖安全的方向复制。
   * 上部寄存器和溢出暂存不能用 R24 等字节别名；通过 A/B 与 WR20 拼接。
   * 此处仅用于常量移位，A/B 不再承载可变移位计数。 */
  if(reg+width<=16) {
    if(right) {
      for(int k=width-1;k>0;--k)we(f,"\tmov\tr%d,r%d\n",reg+k,reg+k-1);
      if(sign)we(f,"\tmovs\twr10,r%d\n\tmov\tr%d,b\n",reg,reg);
      else we(f,"\tmov\tr%d,#0\n",reg);
    } else {
      for(int k=0;k<width-1;++k)we(f,"\tmov\tr%d,r%d\n",reg+k,reg+k+1);
      we(f,"\tmov\tr%d,#0\n",reg+width-1);
    }
    return;
  }
  if(width==4) {
    we(f,"\tmov\twr10,wr%d\n\tmov\tb,a\n\tmov\ta,#0\n\tmov\twr20,wr10\n",reg);
    we(f,"\tmov\twr10,wr%d\n\tmov\ta,b\n\tmovz\twr%d,a\n\torl\twr%d,wr20\n",reg+2,right?reg+2:reg,right?reg+2:reg);
  }
  if(right)we(f,"\tmov\twr10,wr%d\n\tmov\ta,b\n\t%s\twr%d,a\n",reg,sign?"movs":"movz",reg);
  else we(f,"\tmov\twr10,wr%d\n\tmov\tb,a\n\tmov\ta,#0\n\tmov\twr%d,wr10\n",reg+width-2,reg+width-2);
}
static void
wgetbyte(wide_function *f,wide_node *n,symbol *bitStore)
{
  int width=n->left.width,offset=n->right.literal/8,index=width-1-offset,byte=11;
  const bool bit=n->op==GETABIT;
  const char *kind=bit?"getabit":"getbyte";
  wide_value *dest=&f->values[n->result];
  we(f,"; wide %s offset=%d width=%d\n",kind,bit?(int)n->right.literal:offset,width);
  if(n->left.value<0) {
    we(f,"; wide %s path=literal\n\tmov\ta,#0x%02lx\n",
       kind,(unsigned long)((n->left.literal>>(8*offset))&255));
  } else {
    wide_value *v=&f->values[n->left.value];
    if(v->reg<0) {
      char address[32];wa(address,v->slot+index-f->frame-f->stackDepth+1);
      we(f,"; wide %s path=spill slot=%d index=%d\n\tmov\ta,%s\n",kind,v->slot,index,address);
    } else if(v->reg+index<16) {
      byte=v->reg+index;
      we(f,"; wide %s path=byte reg=%d\n",kind,byte);
    } else {
      /* R16以上没有字节别名；先快照包含目标字节的字到保留的B:A。
       * 不移位、不改写原值，结果与输入重叠时也能保留尚未读取的数据。
       */
      int reg=v->reg+index;
      we(f,"; wide %s path=upper reg=%d index=%d\n\tmov\twr10,wr%d\n",kind,v->reg,index,reg&~1);
      byte=(reg&1)?11:10;
    }
  }
  char source[16];
  if(byte==11)strcpy(source,"a");else if(byte==10)strcpy(source,"b");else SNPRINTF(source,sizeof source,"r%d",byte);
  if(bit) {
    /* 在保留的 A/C 中选位，不破坏原宽值或把任意非零字节当作真。
     * 与旧后端使用同一已验证指令序列，所有位都规范化为 0/1。 */
    if(byte!=11)we(f,"\tmov\ta,%s\n",source);
    we(f,"\tmov\tc,acc.%u\n",n->right.literal%8);
    if(bitStore) {
      we(f,"; wide getabit-store fused %s\n\tmov\t%s,c\n",bitStore->rname,bitStore->rname);
      return;
    }
    we(f,"\tclr\ta\n\trlc\ta\n");
    byte=11;strcpy(source,"a");
  }
  if(dest->reg>=0) {
    if(dest->reg!=byte)we(f,"\tmov\tr%d,%s\n",dest->reg,source);
  } else {
    char address[32];wa(address,dest->slot-f->frame-f->stackDepth+1);
    if(byte!=11)we(f,"\tmov\ta,%s\n",source);
    we(f,"\tmov\t%s,a\n",address);
  }
}
static void
wgetword(wide_function *f,wide_node *n)
{
  int width=n->left.width,offset=n->right.literal/8,index=width-2-offset;
  we(f,"; wide getword offset=%d width=%d\n",offset,width);
  if(n->left.value<0) {
    wi(f,16,2,n->left.literal>>(8*offset));wstore(f,n->result,16);return;
  }
  wide_value *v=&f->values[n->left.value];
  if(v->reg<0) {
    /* 槽按大端保存；只读取切片的两个字节，偏移不要求字对齐。
     * 沿用统一 SPX 深度规则，不读取槽外字节或扩展成易失访问。 */
    char address[32];wa(address,v->slot+index-f->frame-f->stackDepth+1);
    we(f,"; wide getword path=spill slot=%d index=%d\n\tmov\twr16,%s\n",v->slot,index,address);
    wstore(f,n->result,16);return;
  }
  if(!(index&1)) {
    we(f,"; wide getword path=word reg=%d\n",v->reg+index);
    wstore(f,n->result,v->reg+index);return;
  }
  if(v->reg+width<=16) {
    /* A/B 不参与分配；先快照两字节，结果与原值重叠也不会丢输入。 */
    we(f,"; wide getword path=bytes reg=%d\n\tmov\tb,r%d\n\tmov\ta,r%d\n",
       v->reg,v->reg+index,v->reg+index+1);
    wstore(f,n->result,10);return;
  }
  /* 上部寄存器没有单字节别名。保留高字的低字节和低字的高字节，
   * 在保留 WR16/WR20 中拼接，不移动整个输入或计算无用的高字。 */
  we(f,"; wide getword path=upper reg=%d\n",v->reg);
  we(f,"\tmov\twr10,wr%d\n\tmov\tb,a\n\tmov\ta,#0\n\tmov\twr16,wr10\n",v->reg);
  we(f,"\tmov\twr10,wr%d\n\tmov\ta,b\n\tmovz\twr20,a\n\torl\twr16,wr20\n",v->reg+2);
  wstore(f,n->result,16);
}
static void
wshift(wide_function *f,wide_node *n)
{
  wide_value *dest=&f->values[n->result];int width=n->width==1?2:n->width;
  int work=dest->reg>=0&&n->width>1?dest->reg:16;
  bool right=n->op==RIGHT_OP;
  if(right && n->width==4 && !n->left.sign && n->right.value<0 &&
     n->right.literal>=24 && n->right.literal<32) {
    /* 无符号高位移只剩最高字节有贡献。先取样该字节再写目标，兼容
     * 输入/结果重叠、上组寄存器和 spill；旋转后的无关位显式掩掉。 */
    unsigned count=n->right.literal-24;
    we(f,"; wide high-byte right shift count=%u\n",n->right.literal);
    if(n->left.value<0)we(f,"\tmov\ta,#0x%02lx\n",(unsigned long)(n->left.literal>>24));
    else {
      wide_value *src=&f->values[n->left.value];
      if(src->reg<0) {char a[32];wa(a,src->slot-f->frame-f->stackDepth+1);we(f,"\tmov\ta,%s\n",a);}
      else if(src->reg<16)we(f,"\tmov\ta,r%d\n",src->reg);
      else we(f,"\tmov\twr10,wr%d\n\tmov\ta,b\n",src->reg);
    }
    if(count>=6)for(unsigned k=count;k<8;++k)we(f,"\trl\ta\n");
    else {unsigned k=count;if(k>=4) {we(f,"\tswap\ta\n");k-=4;}while(k--)we(f,"\trr\ta\n");}
    if(count)we(f,"\tanl\ta,#0x%02x\n",255u>>count);
    we(f,"\tmovz\twr18,a\n");wi(f,16,2,0);wstore(f,n->result,16);return;
  }
  bool loadClobbersA=n->left.value>=0 && n->left.width==1 && f->values[n->left.value].reg<0;
  if(n->right.value>=0) {
    /* B 不参与分配；溢出字节的装载会使用 A，故计数暂存 B。 */
    wload(f,n->right,20,2);we(f,"\tmov\twr10,wr20\n");
    if(loadClobbersA)we(f,"\tmov\tb,a\n");
  }
  wload(f,n->left,work,width);
  if(n->right.value<0) {
    unsigned count=n->right.literal;
    if(count>=8 && count<(unsigned)(width*8)) {
      if(width==4 && count>=16) {
        we(f,"\tmov\twr%d,wr%d\n",right?work+2:work,right?work:work+2);
        if(right && n->left.sign)wsign_word(f,work,work+2);
        else wi(f,right?work:work+2,2,0);
        count-=16;
      }
      if(count>=8) {wshift_byte(f,work,width,right,n->left.sign);count-=8;}
    } else if(width==4 && count>=16 && (!right || !n->left.sign)) {
      we(f,"\tmov\twr%d,wr%d\n",right?work+2:work,right?work:work+2);
      wi(f,right?work:work+2,2,0);count-=16;
    }
    if(count<=4) {while(count--)wshift_one(f,work,width,right,n->left.sign);}
    else {int loop=wt(f);we(f,"\tmov\ta,#%u\n",count);wl(f,loop);wshift_one(f,work,width,right,n->left.sign);we(f,"\tdec\ta\n");wj(f,"jnz",loop);}
  } else {
    int skip=wt(f),loop=wt(f);if(loadClobbersA)we(f,"\tmov\ta,b\n");wj(f,"jz",skip);wl(f,loop);wshift_one(f,work,width,right,n->left.sign);we(f,"\tdec\ta\n");wj(f,"jnz",loop);wl(f,skip);
  }
  wstore(f,n->result,work);
}

static void
wconstant_multiply(wide_function *f,wide_node *n)
{
  wide_value *v=&f->values[n->result];int work=v->reg>=0?v->reg:16;
  we(f,"; wide constant multiply: 0x%08lx\n",(unsigned long)n->right.literal);
  if(n->multiplyTop<0)wi(f,work,4,0);
  else {
    int input=wread(f,n->left,20,4);bool workHasInput=input==work;
    if(input==work) {we(f,"\tmov\tdr20,dr%d\n",input);input=20;}
    if(n->multiplyDigits[n->multiplyTop]<0) {wi(f,work,4,0);we(f,"\tsub\tdr%d,dr%d\n",work,input);}
    else if(!workHasInput)we(f,"\tmov\tdr%d,dr%d\n",work,input);
    for(int k=n->multiplyTop;k-->0;) {
      we(f,"\tadd\tdr%d,dr%d\n",work,work);
      if(n->multiplyDigits[k])we(f,"\t%s\tdr%d,dr%d\n",n->multiplyDigits[k]>0?"add":"sub",work,input);
    }
  }
  wstore(f,n->result,work);
}

static void
wargument(wide_function *f,wide_operand o,int index)
{
  int dest=(index?16:8)-o.width;
  if(o.width!=1) {wload(f,o,dest,o.width);return;}
  if(o.value<0) we(f,"\tmov\tr%d,#0x%02lx\n",dest,(unsigned long)o.literal);
  else {
    wide_value *v=&f->values[o.value];
    if(v->reg>=0) {if(v->reg!=dest)we(f,"\tmov\tr%d,r%d\n",dest,v->reg);}
    else {char address[32];wa(address,v->slot-f->frame-f->stackDepth+1);we(f,"\tmov\ta,%s\n\tmov\tr%d,a\n",address,dest);}
  }
}
static uint32_t wsource_mask(wide_function *f,wide_operand o) {
  return o.value>=0 && f->values[o.value].reg>=0?wide_mask(f->values[o.value].reg,o.width):0;
}
static void
warguments(wide_function *f,wide_node *n)
{
  if(!n->args)return;
  if(n->args==1) {wargument(f,n->left,0);return;}
  bool firstClobbers=wide_mask(8-n->left.width,n->left.width)&wsource_mask(f,n->right);
  bool secondClobbers=wide_mask(16-n->right.width,n->right.width)&wsource_mask(f,n->left);
  if(firstClobbers && secondClobbers) {
    /* DR16 不参与分配，循环复制只暂存一个源即可打破依赖。 */
    wload(f,n->left,16,n->left.width==1?2:n->left.width);
    wargument(f,n->right,1);
    if(n->left.width==1)we(f,"\tmov\twr10,wr16\n\tmov\tr7,a\n");
    else we(f,"\tmov\t%s%d,%s16\n",wp(n->left.width),8-n->left.width,wp(n->left.width));
  } else if(firstClobbers) {wargument(f,n->right,1);wargument(f,n->left,0);}
  else {wargument(f,n->left,0);wargument(f,n->right,1);}
}
static void
wstack_argument(wide_function *f,wide_operand o,unsigned bytes)
{
  /* 先保存活寄存器，再逆序压实参；每次取 spill 都使用更新后的深度。
   * PUSH WR/DR 与 ABI4 标量的大端字节布局相同，不借用分配寄存器。 */
  if(bytes==3) {
    /* 只压入地址的高字节及低字，不压 MIR 的零填充字节。
     * 高寄存器没有字节别名，借用保留的 B:A，不破坏待发送的实参。 */
    wassert(o.width==4);
    int reg=wread(f,o,16,4);
    we(f,"\tmov\twr10,wr%d\n\tpush\tacc\n\tpush\twr%d\n",reg,reg+2);
  } else if(o.width==1) {
    if(o.value>=0 && f->values[o.value].reg>=0)we(f,"\tpush\tr%d\n",f->values[o.value].reg);
    else {wload(f,o,16,2);we(f,"\tmov\twr10,wr16\n\tpush\tacc\n");}
  } else {int reg=wread(f,o,16,o.width);we(f,"\tpush\t%s%d\n",wp(o.width),reg);}
  f->stackDepth+=bytes;
}

/* ABI2 的逻辑低字节依次在 DPL/DPH/B/A。已分配低组且与 B:A
 * 不重叠的纯值可直接搬运；不改变任何内存取样或栈上实参的顺序。
 * 常量已由前端按声明宽度序列化，浮点按位运输，远指针只传三字节。
 * 高组按 WR 分片运输，不生成不存在的字节别名。试验开关关闭、
 * spill 或寄存器别名冲突时保留原快照路径。 */
static bool
wordinary_argument_direct(wide_function *f,wide_operand o,unsigned bytes)
{
  if(!mcs251WideOrdinaryEnabled)return false;
  wassert(bytes>=1 && bytes<=4 && bytes<=(unsigned)o.width);
  if(o.value<0) {
    uint32_t bits=o.literal;
    we(f,"; wide ordinary argument direct literal physical=%u\n",bytes);
    if(bytes==1)we(f,"\tmov\tdpl,#0x%02lx\n",(unsigned long)(bits&255));
    else we(f,"\tmov\tdptr,#0x%04lx\n",(unsigned long)(bits&65535));
    if(bytes>2)we(f,"\tmov\tb,#0x%02lx\n",(unsigned long)((bits>>16)&255));
    if(bytes==4)we(f,"\tmov\ta,#0x%02lx\n",(unsigned long)(bits>>24));
    return true;
  }
  int reg=f->values[o.value].reg;
  if(reg<0 || (wide_mask(reg,o.width)&wide_mask(10,2)))return false;
  if(reg+o.width>16) {
    if(o.width==1)return false;
    we(f,"; wide ordinary argument direct words reg=%d physical=%u\n",reg,bytes);
    we(f,"\tmov\twr10,wr%d\n\tmov\tdpl,a\n",reg+(bytes>2?2:0));
    if(bytes>1)we(f,"\tmov\tdph,b\n");
    if(bytes==3)we(f,"\tmov\twr10,wr%d\n\tmov\tb,a\n",reg);
    else if(bytes==4)we(f,"\tmov\twr10,wr%d\n\txch\ta,b\n",reg);
    return true;
  }
  static const char *dest[]={"dpl","dph","b","a"};
  we(f,"; wide ordinary argument direct reg=%d physical=%u\n",reg,bytes);
  for(unsigned k=0;k<bytes;++k)we(f,"\tmov\t%s,r%d\n",dest[k],reg+o.width-1-k);
  return true;
}

static bool
wordinary_result_direct(wide_function *f,int result,unsigned bytes)
{
  if(!mcs251WideOrdinaryEnabled)return false;
  wide_value *v=&f->values[result];
  wassert(bytes>=1 && bytes<=4 && (bytes==(unsigned)v->width || (bytes==3 && v->pointer && v->width==4)));
  if(v->reg<0 || (wide_mask(v->reg,v->width)&wide_mask(10,2)))return false;
  if(v->reg+v->width>16) {
    if(v->width==1)return false;
    we(f,"; wide ordinary result direct words reg=%d physical=%u\n",v->reg,bytes);
    if(bytes==4)we(f,"\txch\ta,b\n\tmov\twr%d,wr10\n",v->reg);
    else if(bytes==3)we(f,"\tmovz\twr%d,r10\n",v->reg);
    we(f,"\tmov\ta,dpl\n\tmov\tb,dph\n\tmov\twr%d,wr10\n",v->reg+(bytes>2?2:0));
    return true;
  }
  static const char *source[]={"dpl","dph","b","a"};
  we(f,"; wide ordinary result direct reg=%d physical=%u\n",v->reg,bytes);
  if(bytes==3)we(f,"\tmov\tr%d,#0\n",v->reg);
  for(unsigned k=0;k<bytes;++k)we(f,"\tmov\tr%d,%s\n",v->reg+v->width-1-k,source[k]);
  return true;
}

static bool
wordinary_forward_return(wide_function *f,int index)
{
  wide_node *call=&f->nodes[index];
  if(!mcs251WideOrdinaryEnabled || !f->ordinaryEntry || !call->ordinaryCall ||
     call->result<0 || index+1>=f->nn)return false;
  wide_node *ret=&f->nodes[index+1];
  if(ret->op!=RETURN || ret->left.value!=call->result ||
     f->values[call->result].reads!=1 || ret->width!=call->width ||
     getSize(currFunc->type->next)!=getSize(call->callee->type->next))return false;
  /* 只连接紧邻且唯一使用的原样返回，不跨转换、内存写回或控制流。
   * 调用后若还有其他活值，继续原保存/重组路径，不能借此删掉恢复。 */
  for(int v=0;v<f->nv;++v)
    if(v!=call->result && wb_has(call->out,v))return false;
  return true;
}

static bool
wcall(wide_function *f,int index)
{
  wide_node *n=&f->nodes[index];
  bool tail=false;
  /* 局部对象地址可能经别名交给被调函数；在证明其不逃逸前，
   * 不能提前释放当前帧后尾跳转，否则被调函数会覆盖仍活跃的对象。 */
  if(!f->ordinaryEntry && !n->ordinaryCall && !f->no && !f->formalAddress && !n->stackBytes && index+1<f->nn && f->nodes[index+1].op==RETURN) {
    wide_node *ret=&f->nodes[index+1];
    tail=(n->result>=0 && ret->left.value==n->result && f->values[n->result].reads==1 &&
          ret->width==wide_width(currFunc->type->next)) ||
         (n->result<0 && !ret->width && IS_VOID(currFunc->type->next));
  }
  int saved[WV_MAX],count=0;
  uint32_t liveBytes=0,omitted=0,calleeWrites=UINT32_MAX;
  unsigned omittedGroups=0,omittedBytes=0;
  bool knownLeaf=wide_leaf_writes(n->callee,&calleeWrites);
  wassert(!f->stackDepth);
  if(!tail)for(int i=0;i<f->nv;++i) {
    wide_value *v=&f->values[i];
    if(i!=n->result && wb_has(n->out,i) && v->reg>=0) {
      uint32_t group=mcs251RegisterBytes(v->reg,v->width);liveBytes|=group;
      if(f->applyCallEffects && knownLeaf && (n->callSaveOmit&group)==group) {
        omitted|=group;++omittedGroups;omittedBytes+=v->width;continue;
      }
      we(f,"\tpush\t%s%d\n",v->width==1?"r":wp(v->width),v->reg);
      saved[count++]=i;f->stackDepth+=v->width;
    }
  }
  size_t setupStart=dbuf_get_length(&f->output);
  if(n->stackArgs)we(f,"; wide stack call args=%u bytes=%u\n",n->stackArgs,n->stackBytes);
  for(unsigned k=0;k<n->stackArgs;++k)wstack_argument(f,n->stackOperands[k],n->stackWidths[k]);
  if(n->ordinaryCall) {
    we(f,"; wide ordinary ABI call: args=%u stack=%u\n",n->args,n->stackBytes);
    if(n->args) {
      /* 先快照全部首参，再改 A/B；不能让字节拆分覆盖尚未读取的源。
       * DPL/DPH 是独立 SFR，B/A 对应 R10/R11；只借用保留 DR16。 */
      unsigned bytes=getSize(FUNC_ARGS(n->callee->type)->type);
      if(!wordinary_argument_direct(f,n->left,bytes)) {
      wload(f,n->left,16,bytes==1?2:n->left.width);
      we(f,"\tmov\twr10,wr%d\n\tmov\tdpl,a\n",bytes>2?18:16);
      if(bytes>1)we(f,"\tmov\tdph,b\n");
      if(bytes==3)we(f,"\tmov\twr10,wr16\n\tmov\tb,a\n");
      else if(bytes==4)we(f,"\tmov\twr10,wr16\n\txch\ta,b\n");
      }
    }
  } else warguments(f,n);
  if(tail) {
    if(f->frame)we(f,"\tsub\tspx,#%d\n",f->frame);
    we(f,"\tejmp\t%s\n",n->callee->rname);return true;
  }
  size_t setupEnd=dbuf_get_length(&f->output);
  we(f,"\tecall\t%s\n",n->callee->rname);
  size_t finishStart=dbuf_get_length(&f->output);
  if(n->stackBytes) {we(f,"\tsub\tspx,#%u\n",n->stackBytes);f->stackDepth-=n->stackBytes;}
  if(wordinary_forward_return(f,index)) {
    /* 保留真正的 CALL、完整实参及调用期间的本地帧。被调函数仍可访问
     * 当前局部对象/取址形参；返回后才清理栈，不把此优化变成尾跳转。
     * 同一 ABI 的物理返回字节保持原位，不构造再拆开未被使用的 MIR 值。 */
    wassert(!count && !f->stackDepth);
    we(f,"; wide ordinary result forwarded physical=%u frame=%d\n",getSize(currFunc->type->next),f->frame);
    if(f->frame)we(f,"\tsub\tspx,#%d\n",f->frame);
    we(f,"\teret\n");return true;
  }
  /* 返回值先移出 ABI 寄存器，再恢复调用者；干涉图保证目标不覆盖活值。 */
  if(n->result>=0) {
    if(n->ordinaryCall) {
      unsigned bytes=getSize(n->callee->type->next);
      if(!wordinary_result_direct(f,n->result,bytes)) {
      /* 在恢复活寄存器前按逻辑低字节 DPL/DPH/B/A 重组成宽值。
       * 指针仅有三字节，显式补零；浮点只运输位模式，不做数值转换。 */
      if(bytes==4)we(f,"\txch\ta,b\n\tmov\twr16,wr10\n");
      else if(bytes==3)we(f,"\tmovz\twr16,r10\n");
      we(f,"\tmov\ta,dpl\n");
      if(bytes==1)we(f,"\tmovz\twr16,a\n");
      else we(f,"\tmov\tb,dph\n\tmov\twr%d,wr10\n",bytes>2?18:16);
      wstore(f,n->result,16);
      }
    } else wstore(f,n->result,n->width==1?6:8-n->width);
  }
  size_t finishEnd=dbuf_get_length(&f->output);
  /* 记录调用者准备参数/接回结果的真实物理写入；它们不属于 callee。
   * 保存按原宽度成组记录，不能把未受影响的几个字节误算为整组可删。 */
  const char *body=dbuf_get_buf(&f->output);
  mx251_effects setup=mx251TextEffects(body+setupStart,setupEnd-setupStart);
  mx251_effects finish=mx251TextEffects(body+finishStart,finishEnd-finishStart);
  if(!f->applyCallEffects && knownLeaf && !setup.unknown && !finish.unknown) {
    uint32_t writes=calleeWrites|setup.writes|finish.writes;
    for(int j=0;j<count;++j) {
      wide_value *v=&f->values[saved[j]];uint32_t group=mcs251RegisterBytes(v->reg,v->width);
      if(!(writes&group))n->callSaveOmit|=group;
    }
  }
  if(omitted) {
    bool valid=knownLeaf && !setup.unknown && !finish.unknown && !(omitted&(calleeWrites|setup.writes|finish.writes));
    if(!valid)f->callEffectsInvalid=true;
    dbuf_printf(&f->output,"; mx251-call-save caller=%s site=%d callee=%s omitted=%08lx groups=%u bytes=%u verified=%u\n",
      currFunc->rname,index,n->callee->rname,(unsigned long)omitted,omittedGroups,omittedBytes,valid);
  }
  dbuf_printf(&f->output,"; mx251-call-effects schema=1 caller=%s site=%d callee=%s abi=%u live=%08lx setup=%08lx finish=%08lx complete=%u saved=",
     currFunc->rname,index,n->callee->rname,(unsigned)FUNC_SDCCCALL(n->callee->type),
     (unsigned long)liveBytes,(unsigned long)setup.writes,(unsigned long)finish.writes,!setup.unknown && !finish.unknown);
  for(int j=0;j<count;++j) {
    wide_value *v=&f->values[saved[j]];
    dbuf_printf(&f->output,"%s%08lx",j?",":"",(unsigned long)mcs251RegisterBytes(v->reg,v->width));
  }
  dbuf_append_str(&f->output,"\n");
  while(count) {
    wide_value *v=&f->values[saved[--count]];
    we(f,"\tpop\t%s%d\n",v->width==1?"r":wp(v->width),v->reg);f->stackDepth-=v->width;
  }
  wassert(!f->stackDepth);return false;
}

static void
wjump_table(wide_function *f,wide_node *n)
{
  int table=wt(f);
  /* 每项固定为四字节 EJMP。完整宽度缩放/地址相加，不让超过 64 项的
   * 偏移在 A 中回绕；只用保留的 DR16/DR20/A，不破坏任一后继的活跃值。
   * 表内不使用短跳转，布局距离和页边界不改变表项宽度。 */
  we(f,"; wide jump table entries=%d successors=%d\n",n->jumpCount,n->jumpCount);
  wload(f,n->left,20,4);
  we(f,"\tadd\tdr20,dr20\n\tadd\tdr20,dr20\n");
  we(f,"\tmov\ta,#(__wm_%s_%d>>16)\n\tmovz\twr16,a\n\tmov\twr18,#__wm_%s_%d\n",currFunc->rname,table,currFunc->rname,table);
  we(f,"\tadd\tdr16,dr20\n\tejmp\t@dr16\n");
  wl(f,table);
  for(int k=0;k<n->jumpCount;++k)wj(f,"ejmp",f->jumpLabels[n->jumpStart+k]);
}

static void
wbit_store(wide_function *f,wide_node *n)
{
  we(f,"; wide bit store %s source-width=%d\n",n->addressSymbol->rname,n->right.width);
  if(n->right.value<0)we(f,"\t%s\t%s\n",n->right.literal?"setb":"clr",n->addressSymbol->rname);
  else {
    int zero=wt(f),done=wt(f);wtest_zero(f,n->right);
    wj(f,"je",zero);we(f,"\tsetb\t%s\n",n->addressSymbol->rname);wj(f,"sjmp",done);
    wl(f,zero);we(f,"\tclr\t%s\n",n->addressSymbol->rname);wl(f,done);
  }
}

/* ABI2 的参数和返回值使用相同物理字节。仅对无副作用、无转换的
 * 单参数直通函数保留这些入口字节，避免先搬入宽寄存器再搬回。
 * 不跨内存、调用、分支或栈帧；指针补位及限定类型继续走已有路径。 */
static bool
wide_ordinary_passthrough(wide_function *f)
{
  value *arg=FUNC_ARGS(currFunc->type);
  sym_link *ret=currFunc->type->next;
  int received=-1,returned=-1;
  if(!f->ordinaryEntry || f->nv!=1 || f->received!=1 || f->no ||
     f->frame || f->formalAddress || !arg || arg->next ||
     !wide_type(arg->type) || !wide_type(ret) || IS_PTR(arg->type) ||
     IS_PTR(ret) || getSize(arg->type)!=getSize(ret) ||
     IS_FLOAT(arg->type)!=IS_FLOAT(ret))return false;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    switch(n->op) {
    case FUNCTION:case ENDFUNCTION:case LABEL:break;
    case RECEIVE:
      if(received>=0 || returned>=0 || n->origin->argreg!=1 ||
         n->width!=getSize(arg->type))return false;
      received=n->result;break;
    case RETURN:
      if(received<0 || returned>=0 || n->left.value!=received ||
         n->width!=getSize(ret))return false;
      returned=received;break;
    default:return false;
    }
  }
  return received>=0 && returned==received;
}

static void
wide_emit(wide_function *f)
{
  we(f,"\n; mcs251 wide-mir accepted: %s; values=%d nodes=%d frame=%d folded-casts=%d\n%s:\n",currFunc->name,f->nv,f->nn,f->frame,f->foldedCasts,currFunc->rname);
  if(f->reusedParameterLoads)we(f,"; wide repeated parameter loads reused=%d\n",f->reusedParameterLoads);
  for(int i=0;i<f->nv;++i)we(f,"; wide v%d %s width=%d reg=%d slot=%d edges=%016llx:%016llx\n",i,f->values[i].sym->name,f->values[i].width,f->values[i].reg,f->values[i].slot,(unsigned long long)f->values[i].edges.w[1],(unsigned long long)f->values[i].edges.w[0]);
  for(int i=0;i<f->no;++i)we(f,"; wide object %s slot=%d size=%d\n",f->objects[i].sym->name,f->objects[i].slot,f->objects[i].size);
  /* 仅在显式 iCode 诊断时输出实际 MIR 活跃集；注释不进入机器码成本。 */
  if(options.dump_i_code)for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    we(f,"; wide node=%d op=%u line=%u depth=%d result=%d left=%d right=%d index=%d succ=%d,%d in=%016llx:%016llx out=%016llx:%016llx def=%016llx:%016llx use=%016llx:%016llx\n",
      i,n->op,n->origin->lineno,n->origin->depth,n->result,n->left.value,n->right.value,n->addressIndex.value,n->succ[0],n->succ[1],
      (unsigned long long)n->in.w[1],(unsigned long long)n->in.w[0],(unsigned long long)n->out.w[1],(unsigned long long)n->out.w[0],
      (unsigned long long)n->def.w[1],(unsigned long long)n->def.w[0],(unsigned long long)n->use.w[1],(unsigned long long)n->use.w[0]);
  }
  if(f->ordinaryEntry)we(f,"; wide ordinary ABI2 entry: first=DPL/DPH/B/A stack=physical\n");
  if(wide_ordinary_passthrough(f)) {
    we(f,"; wide ordinary ABI2 passthrough: entry bytes already match return\n\teret\n");
    return;
  }
  if(f->frame)we(f,"\tadd\tspx,#%d\n",f->frame);
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    f->emitWeight=1+4*n->origin->depth;
    /* 已经规范化的常量选位，只在紧邻且单用的 cast/store 链中直接写 C。
     * 不跨真实读取、标签或调用；目标 bit/SFR 仍发生一次独立写入。
     * 非单用结果仍生成字节 0/1，供其他消费者使用。 */
    if(n->op==GETABIT && f->values[n->result].reads==1 && i+2<f->nn &&
       f->nodes[i+1].op==WM_BIT_CAST && f->nodes[i+1].right.value==n->result &&
       f->values[f->nodes[i+1].result].reads==1 && f->nodes[i+2].op==WM_BIT_STORE &&
       f->nodes[i+2].right.value==f->nodes[i+1].result) {
      wgetbyte(f,n,f->nodes[i+2].addressSymbol);i+=2;continue;
    }
    /* 仅消去紧邻位写入的单用纯转换；仍按转换前的完整宽度判零。
     * 不跨标签/调用/内存节点，不减少实际位写入次数。 */
    if(n->op==WM_BIT_CAST && i+1<f->nn && f->nodes[i+1].op==WM_BIT_STORE &&
       f->nodes[i+1].right.value==n->result && f->values[n->result].reads==1) {
      wide_node memory=f->nodes[++i];memory.right=n->right;
      we(f,"; wide bit cast-store fused\n");wbit_store(f,&memory);continue;
    }
    /* 同一原始赋值的唯一使用就是紧随其后的真实写回；用立即数写入，
     * 保留每个 volatile 字节事务，不将常量值搬进再搬出临时寄存器。 */
    if(n->op=='=' && n->right.value<0 && i+1<f->nn &&
       f->nodes[i+1].op==WM_SYMBOL_STORE && f->nodes[i+1].origin==n->origin &&
       f->nodes[i+1].right.value==n->result && f->values[n->result].reads==1 &&
       n->right.width==n->width) {
      wide_node memory=f->nodes[++i];memory.right=n->right;wmemory_symbol(f,&memory);continue;
    }
    if(n->op==WM_BIT_LOAD && i+1<f->nn && f->nodes[i+1].op==IFX &&
       f->nodes[i+1].left.value==n->result && f->values[n->result].reads==1) {
      wide_node *branch=&f->nodes[++i];
      we(f,"; wide bit branch %s\n\tmov\tc,%s\n",n->addressSymbol->rname,n->addressSymbol->rname);
      wbranch(f,branch->trueBranch?"jc":"jnc",branch->target);continue;
    }
    if(wcompare_op(n->op)) {
      if(n->left.width==1 && n->right.width==1)wcompare_byte(f,n->left,n->right);
      else {
        int width=n->left.width;
        int lhs=wread(f,n->left,16,width),rhs=wread(f,n->right,20,width);
        we(f,"\tcmp\t%s%d,%s%d\n",wp(width),lhs,wp(width),rhs);
      }
      if(i+1<f->nn && f->nodes[i+1].op==IFX && f->nodes[i+1].left.value==n->result && f->values[n->result].reads==1) {
        wide_node *branch=&f->nodes[++i];wbranch(f,wcondition(n->op,n->left.sign,branch->trueBranch),branch->target);
      } else {
        wboolean_result(f,n,wcondition(n->op,n->left.sign,true));
      }
      continue;
    }
    switch(n->op) {
    case WM_NOP:case FUNCTION:case SEND:break;
    case RECEIVE:
      if(f->ordinaryEntry) {
        unsigned bytes=getSize(operandType(IC_RESULT(n->origin)));
        wide_value *v=&f->values[n->result];
        /* 可寻址低组寄存器与 ABI SFR 不重叠，可逐字节直接接收。
         * 高组/溢出值继续走完整快照路径，不生成 R16 以上字节别名。 */
        if(v->reg>=0 && v->reg+v->width<=16) {
          static const char *source[]={"dpl","dph","b","a"};
          if(bytes==3)we(f,"\tmov\tr%d,#0\n",v->reg);
          for(unsigned k=0;k<bytes;++k)we(f,"\tmov\tr%d,%s\n",v->reg+v->width-1-k,source[k]);
          break;
        }
        /* 先消费 A/B 的高半部，再读取 DPL/DPH；三字节指针显式补零。
         * 只搬位模式，浮点不做整数转换。 */
        if(bytes==4)we(f,"\txch\ta,b\n\tmov\twr16,wr10\n");
        else if(bytes==3)we(f,"\tmovz\twr16,r10\n");
        we(f,"\tmov\ta,dpl\n");
        if(bytes==1)we(f,"\tmovz\twr16,a\n");
        else we(f,"\tmov\tb,dph\n\tmov\twr%d,wr10\n",bytes>2?18:16);
        wstore_known(f,n->result,16,true);
      } else if(f->values[n->result].pointer)we(f,"\tanl\twr%d,#0x00ff\n",f->values[n->result].reg);
      break;
    case WM_PARAM_ADDRESS: {
      /* 地址以入口 SPX 为基准，与直接读写用同一个物理槽；
       * 扣除本地帧和动态保存深度，不改变参数布局或传入副本。 */
      int displacement=n->addressOffset-f->frame-f->stackDepth;
      we(f,"; wide parameter address entry-offset=%d\n\tmov\tdr16,spx\n",n->addressOffset);
      if(displacement)we(f,"\t%s\tdr16,#%d\n",displacement<0?"sub":"add",abs(displacement));
      we(f,"\tanl\twr16,#0x00ff\n");wstore_known(f,n->result,16,true);break;
    }
    case WM_PARAM_LOAD: {
      int base=n->addressOffset-f->frame-f->stackDepth;
      int sourceWidth=n->left.width;
      we(f,"; wide stack parameter width=%d result=%d entry-offset=%d\n",sourceWidth,n->width,n->addressOffset);
      /* 参数读取也可能已与整数 CAST 合并。保留物理源宽度、符号和字节序，
       * 不能按目的宽度读取相邻参数或把有符号窄值当作零扩展。 */
      if(n->width==1) {char a[32];wa(a,base+sourceWidth-1);we(f,"\tmov\ta,%s\n\tmovz\twr16,a\n",a);}
      else if(sourceWidth>=n->width)for(int k=0;k<n->width;k+=2) {
        char a[32];wa(a,base+sourceWidth-n->width+k);we(f,"\tmov\twr%d,%s\n",16+k,a);
      } else if(sourceWidth==3) {
        /* 三字节 ABI 指针高字节零扩展，低字从下一地址读取。
         * 不读邻近参数，不把物理栈宽度误当成四字节 MIR 宽度。 */
        char high[32],low[32];wa(high,base);wa(low,base+1);
        we(f,"\tmov\ta,%s\n\tmovz\twr16,a\n\tmov\twr18,%s\n",high,low);
      } else {
        int low=16+n->width-2;char a[32];wa(a,base);
        if(sourceWidth==1)we(f,"\tmov\ta,%s\n\t%s\twr%d,a\n",a,n->left.sign?"movs":"movz",low);
        else we(f,"\tmov\twr%d,%s\n",low,a);
        if(n->width==4) {if(n->left.sign)wsign_word(f,16,low);else wi(f,16,2,0);}
      }
      wstore(f,n->result,16);break;
    }
    case WM_PARAM_STORE: {
      int base=n->addressOffset-f->frame-f->stackDepth,physical=n->left.width;
      wload(f,n->right,16,n->width==1?2:n->width);
      we(f,"; wide stack parameter store width=%d entry-offset=%d\n",physical,n->addressOffset);
      if(physical==1 || physical==3) {
        char a[32];wa(a,base);
        /* WR16 以上没有可编码的字节寄存器别名，经保留 WR10 取低字节。 */
        we(f,"\tmov\twr10,wr16\n\tmov\t%s,a\n",a);
      }
      for(int k=physical==3?1:0;physical!=1 && k<physical;k+=2) {
        char a[32];wa(a,base+k);
        we(f,"\tmov\t%s,wr%d\n",a,16+k+(physical==3));
      }
      break;
    }
    case WM_OBJECT_LOAD:case WM_OBJECT_STORE: {
      /* 直接标量与指针间接访问共用对象槽及同一发射器，不另建栈副本。 */
      wide_node memory=*n;memory.op=n->op==WM_OBJECT_STORE?WM_STORE:WM_LOAD;
      we(f,"; wide local snapshot %s %s\n",n->op==WM_OBJECT_STORE?"store":"load",n->addressSymbol->rname);
      wmemory_stack(f,&memory);break;
    }
    case WM_STORE: {
      int last=wmemory_store_chain(f,i);
      if(last>i)i=last;else wmemory(f,n,false);
      break;
    }
    case WM_LOAD:wmemory(f,n,false);break;
    case WM_SYMBOL_LOAD:case WM_SYMBOL_STORE:wmemory_symbol(f,n);break;
    case WM_BIT_CAST:
      wtest_zero(f,n->right);wboolean_result(f,n,"jne");break;
    case WM_BIT_LOAD:
      /* A 为保留寄存器；一次 MOV C,bit 读取，不读取整个 SFR 或相邻位。 */
      we(f,"; wide bit load %s\n\tclr\ta\n\tmov\tc,%s\n\trlc\ta\n",n->addressSymbol->rname,n->addressSymbol->rname);
      wstore_byte(f,n->result,11);break;
    case WM_BIT_STORE:wbit_store(f,n);break;
    case WM_ADDRESS:
      if(n->addressObject>=0) {
        int displacement=f->objects[n->addressObject].slot+n->addressOffset-f->frame-f->stackDepth+1;
        int dest=n->addressOp && f->values[n->result].reg>=0?f->values[n->result].reg:16;
        if(n->addressOp) {
          we(f,"; wide stack indexed base object=%d offset=%d op=%c index-width=%d\n",n->addressObject,n->addressOffset,n->addressOp,n->addressIndex.width);
          wload(f,n->addressIndex,20,4);
        }
        we(f,"\tmov\tdr%d,spx\n",dest);
        if(displacement)we(f,"\t%s\tdr%d,#%d\n",displacement<0?"sub":"add",dest,abs(displacement));
        if(n->addressOp)we(f,"\t%s\tdr%d,dr20\n",n->addressOp=='+'?"add":"sub",dest);
        we(f,"\tanl\twr%d,#0x00ff\n",dest);
        wstore_known(f,n->result,dest,true);break;
      }
      /* ASxxxx 的高字节重定位只用于字节操作数，不能把 >>16 放入字立即数。 */
      we(f,"\tmov\ta,#((%s%+d)>>16)\n\tmovz\twr16,a\n\tmov\twr18,#(%s%+d)\n",n->addressSymbol->rname,n->addressOffset,n->addressSymbol->rname,n->addressOffset);
      wstore_known(f,n->result,16,true);break;
    case ENDFUNCTION:
      /* void 函数的隐式返回没有 RETURN iCode，也必须释放局部帧。 */
      if(IS_VOID(currFunc->type->next)) {
        if(f->frame)we(f,"\tsub\tspx,#%d\n",f->frame);
        we(f,"\teret\n");
      }
      break;
    case CALL:if(wcall(f,i))++i;break;
    case '*':wconstant_multiply(f,n);break;
    case WM_UMULW:wunsigned_multiply(f,n);break;
    case GETABIT:case GETBYTE:wgetbyte(f,n,NULL);break;
    case GETWORD:wgetword(f,n);break;
    case JUMPTABLE:wjump_table(f,n);break;
    case LABEL:wl(f,n->label);break;
    case GOTO:
      if(i+1==f->nn || f->nodes[i+1].label!=n->target) {
        wide_branch_site site={n->target,NULL,dbuf_get_length(&f->output),0,f->emitUpper,0,f->emitBarrier,f->emitWeight};
        wj(f,"ejmp",n->target);site.end=dbuf_get_length(&f->output);site.upperEnd=f->emitUpper;
        if(f->branchCount<WN_MAX)f->branches[f->branchCount++]=site;else f->relaxDisabled=true;
      }
      break;
    case RETURN:
      /* ABI4 的字节返回仅规定 R7；有符号扩展由使用者按类型完成。 */
      if(f->ordinaryEntry && n->width) {
        unsigned bytes=getSize(currFunc->type->next);
        int reg=n->left.value>=0?f->values[n->left.value].reg:-1;
        we(f,"; wide ordinary ABI2 return physical=%u\n",bytes);
        if(n->left.value<0) {
          /* 常量按其已序列化位模式直接写 ABI 寄存器；浮点不数值转换。 */
          uint32_t bits=n->left.literal;
          if(bytes==1)we(f,"\tmov\tdpl,#0x%02lx\n",(unsigned long)(bits&255));
          else we(f,"\tmov\tdptr,#0x%04lx\n",(unsigned long)(bits&65535));
          if(bytes>2)we(f,"\tmov\tb,#0x%02lx\n",(unsigned long)((bits>>16)&255));
          if(bytes==4)we(f,"\tmov\ta,#0x%02lx\n",(unsigned long)(bits>>24));
        } else if(reg>=0 && reg+n->left.width<=16) {
          static const char *dest[]={"dpl","dph","b","a"};
          for(unsigned k=0;k<bytes;++k)we(f,"\tmov\t%s,r%d\n",dest[k],reg+n->left.width-1-k);
        } else {
          wload(f,n->left,16,bytes==1?2:n->left.width);
          we(f,"\tmov\twr10,wr%d\n\tmov\tdpl,a\n",bytes>2?18:16);
          if(bytes>1)we(f,"\tmov\tdph,b\n");
          if(bytes==3)we(f,"\tmov\twr10,wr16\n\tmov\tb,a\n");
          else if(bytes==4)we(f,"\tmov\twr10,wr16\n\txch\ta,b\n");
        }
      } else if(n->width==1)wload_byte(f,n->left,7);
      else if(n->width)wload(f,n->left,8-n->width,n->width);
      if(f->frame)we(f,"\tsub\tspx,#%d\n",f->frame);
      we(f,"\teret\n");break;
    case IFX: {
      wtest_zero(f,n->left);wbranch(f,n->trueBranch?"jne":"je",n->target);break;
    }
    case '=':case CAST: {
      wide_value *v=&f->values[n->result];int width=v->width==1?2:v->width;
      if(v->width==1 && n->right.width==1) {
        int work=v->reg>=0?v->reg:11;we(f,"; wide byte copy\n");
        wload_byte(f,n->right,work);wstore_byte(f,n->result,work);break;
      }
      /* 字节寄存器只能寻址 R0..R15；宽寄存器高组继续走原有提取路径。 */
      if(v->width==1 && v->reg>=0 && n->right.value>=0 && f->values[n->right.value].reg>=0) {
        int byte=f->values[n->right.value].reg+n->right.width-1;
        if(byte<16) {if(byte!=v->reg)we(f,"\tmov\tr%d,r%d\n",v->reg,byte);break;}
      }
      int work=v->reg>=0&&v->width>1?v->reg:16;
      wload(f,n->right,work,width);wstore_known(f,n->result,work,wcanonical_pointer(f,n->right));break;
    }
    case LEFT_OP:case RIGHT_OP:wshift(f,n);break;
    case '!': {
      wtest_zero(f,n->left);wboolean_result(f,n,"je");break;
    }
    default:wbinary(f,n);break;
    }
  }
  wrelax_branches(f);
}
