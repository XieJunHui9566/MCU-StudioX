/* SPDX-License-Identifier: GPL-2.0-or-later
 * 宽发射器的 Source 模式字节成本。不是指令合法性检查或芯片周期模型。
 * 符号形式只接受调用方按类型提供的 direct/bit 事实；不猜测名称或链接地址。
 * 未知表达式返回未知，禁止按零计价。无上下文入口保持保守行为。
 * 分支仍用原来的保守范围证明；此接口在缩短完成后读取最终文本。
 */
#ifndef MCS251_WIDE_COST_H
#define MCS251_WIDE_COST_H
#include <ctype.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>
typedef struct { unsigned bytes, unknown; } wide_byte_cost;
typedef struct { int kind, width, reg; } wide_cost_operand;
enum { WC_UNKNOWN, WC_REG, WC_IMM, WC_DIRECT, WC_INDIRECT, WC_INDEXED, WC_DPTR, WC_CARRY, WC_BIT };
typedef wide_cost_operand (*wide_cost_resolver)(const char *,const void *);

/* 只识别发射器使用的 symbol、(symbol) 和 (symbol+十进制偏移)。
 * C 符号区分大小写；偏移必须仍在已声明对象内，不接受任意汇编表达式。 */
static int
wide_cost_symbol_offset(const char *operand,const char *name,unsigned size)
{
  const char *p=operand;int paren=*p=='(';unsigned offset=0;
  if(paren)++p;
  size_t length=strlen(name);
  if(!length || strncmp(p,name,length))return -1;
  p+=length;
  if(*p=='+') {
    ++p;if(!isdigit((unsigned char)*p))return -1;
    do {unsigned digit=(unsigned)(*p++-'0');if(offset>(INT_MAX-digit)/10u)return -1;offset=offset*10+digit;} while(isdigit((unsigned char)*p));
  }
  if(paren) {if(*p!=')')return -1;++p;}
  return !*p && offset<size?(int)offset:-1;
}

static wide_cost_operand
wide_cost_arg(const char *s)
{
  wide_cost_operand a={WC_UNKNOWN,0,0}; char *end; long n;
  if(!*s)return a;
  if(*s=='#' && s[1]) {a.kind=WC_IMM;return a;}
  if(!strcmp(s,"a")) {a.kind=WC_REG;a.width=1;a.reg=11;return a;}
  if(!strcmp(s,"spx") || !strcmp(s,"dpx")) {a.kind=WC_REG;a.width=4;a.reg=s[0]=='s'?60:56;return a;}
  if(!strcmp(s,"dptr")) {a.kind=WC_DPTR;return a;}
  if(!strcmp(s,"c") || !strcmp(s,"cy")) {a.kind=WC_CARRY;return a;}
  if(*s=='r' || ((*s=='w' || *s=='d') && s[1]=='r')) {
    int width=*s=='r'?1:(*s=='w'?2:4); const char *digits=s+(width==1?1:2);
    if(!isdigit((unsigned char)*digits))return a;
    n=strtol(digits,&end,10);
    if(*end || n<0 || n%width || (width==1?n>15:width==2?n>30:(n>28 && n!=56 && n!=60)))return a;
    a.kind=WC_REG;a.width=width;a.reg=(int)n;return a;
  }
  if(*s=='@') {
    char base[32]; size_t k=0; s++;
    while(s[k] && s[k]!='+' && s[k]!='-') {if(k+1>=sizeof base)return a;base[k]=s[k];++k;}
    base[k]=0;a=wide_cost_arg(base);
    if(a.kind!=WC_REG || a.width==1) {a.kind=WC_UNKNOWN;return a;}
    if(s[k] && !s[k+1]) {a.kind=WC_UNKNOWN;return a;}
    a.kind=s[k]?WC_INDEXED:WC_INDIRECT;return a;
  }
  if(!strcmp(s,"b") || !strcmp(s,"acc") || !strcmp(s,"dpl") || !strcmp(s,"dph")) {a.kind=WC_DIRECT;return a;}
  /* 强制 direct 的汇编拼写可独立确定长度；未解析的普通符号不猜测存储空间。 */
  if((*s=='*' && s[1]) || (!strncmp(s,"s:",2) && s[2])) {a.kind=WC_DIRECT;return a;}
  n=strtol(s,&end,0);
  if(end!=s && !*end && n>=0 && n<=255)a.kind=WC_DIRECT;
  return a;
}

/* 返回0表示未知。输入必须是一条已由宽后端生成的指令，不包含标签或注释。 */
static unsigned
wide_instruction_bytes_context(const char *text,size_t length,wide_cost_resolver resolve,const void *context)
{
  char op[16],args[256],original[256];size_t k=0,j=0;const char *p=text,*limit=text+length;
  while(p<limit && isspace((unsigned char)*p))++p;
  while(p<limit && isalpha((unsigned char)*p)) {if(k+1>=sizeof op)return 0;op[k++]=(char)tolower((unsigned char)*p++);}
  op[k]=0;if(!k || (p<limit && !isspace((unsigned char)*p)))return 0;
  while(p<limit && *p!=';') {if(!isspace((unsigned char)*p)) {if(j+1>=sizeof args)return 0;original[j]=*p;args[j++]=(char)tolower((unsigned char)*p);}++p;}
  args[j]=original[j]=0;char *second=strchr(args,',');if(second) {*second++=0;if(strchr(second,','))return 0;}else second=args+j;
  char *originalSecond=strchr(original,',');if(originalSecond)*originalSecond++=0;else originalSecond=original+j;
  wide_cost_operand a=wide_cost_arg(args),b=wide_cost_arg(second);
  if(resolve) {
    if(a.kind==WC_UNKNOWN)a=resolve(original,context);
    if(b.kind==WC_UNKNOWN)b=resolve(originalSecond,context);
  }
  /* ACC.0 是发射器固定使用的经典位地址，不依赖用户符号。 */
  if(!strcmp(args,"acc.0"))a=(wide_cost_operand){WC_BIT,0,0};
  if(!strcmp(second,"acc.0"))b=(wide_cost_operand){WC_BIT,0,0};
#define WC_OP(s) (!strcmp(op,(s)))
  if(WC_OP("eret") && !*args)return 1;
  if(WC_OP("ejmp") || WC_OP("ecall")) {
    if(!*args || *second)return 0;
    if(a.kind==WC_INDIRECT && a.width==4)return 2;
    return *args!='@'?4:0;
  }
  if(WC_OP("jc") || WC_OP("jnc") || WC_OP("jz") || WC_OP("jnz") || WC_OP("sjmp") ||
     WC_OP("je") || WC_OP("jne") || WC_OP("jg") || WC_OP("jle") || WC_OP("jsl") ||
     WC_OP("jsge") || WC_OP("jsg") || WC_OP("jsle"))return *args && !*second?2:0;
  if(WC_OP("movc"))return !strcmp(args,"a") && (!strcmp(second,"@a+dptr") || !strcmp(second,"@a+pc"))?1:0;
  if(WC_OP("rl") || WC_OP("rr") || WC_OP("rlc") || WC_OP("rrc") || WC_OP("swap"))return a.kind==WC_REG && a.reg==11 && a.width==1 && !*second?1:0;
  if(WC_OP("clr"))return *second?0:a.kind==WC_BIT?2:((a.kind==WC_REG && a.reg==11 && a.width==1) || a.kind==WC_CARRY)?1:0;
  if(WC_OP("setb"))return *second?0:a.kind==WC_BIT?2:a.kind==WC_CARRY?1:0;
  if(WC_OP("push") || WC_OP("pop"))return !*second && (a.kind==WC_REG || a.kind==WC_DIRECT)?2:0;
  if(WC_OP("sra") || WC_OP("srl"))return a.kind==WC_REG && a.width<=2 && !*second?2:0;
  if(WC_OP("inc") || WC_OP("dec")) {
    if(*second && strcmp(second,"#1") && strcmp(second,"#2") && strcmp(second,"#4"))return 0;
    if(a.kind==WC_REG)return a.width==1 && a.reg==11 && (!*second || !strcmp(second,"#1"))?1:2;
    return 0;
  }
  if(WC_OP("movs") || WC_OP("movz"))return a.kind==WC_REG && a.width==2 && b.kind==WC_REG && b.width==1?2:0;
  if(WC_OP("mul"))return a.kind==WC_REG && b.kind==WC_REG && a.width==b.width && a.width<=2?2:0;
  if(WC_OP("xch"))return a.kind==WC_REG && a.width==1 && a.reg==11 && (b.kind==WC_DIRECT || (b.kind==WC_REG && b.width==1 && b.reg<8))?2:0;
  if(WC_OP("mov")) {
    if((a.kind==WC_CARRY && b.kind==WC_BIT) || (a.kind==WC_BIT && b.kind==WC_CARRY))return 2;
    if(a.kind==WC_DPTR && b.kind==WC_IMM)return 3;
    if(a.kind==WC_REG && b.kind==WC_REG && a.width==b.width)return 2;
    if(a.kind==WC_REG && b.kind==WC_IMM)return a.width==1?(a.reg==11?2:3):4;
    if(a.kind==WC_REG && b.kind==WC_DIRECT)return a.width==1 && a.reg==11?2:3;
    if(a.kind==WC_DIRECT && b.kind==WC_REG)return b.width==1 && b.reg==11?2:3;
    if(a.kind==WC_DIRECT && (b.kind==WC_IMM || b.kind==WC_DIRECT))return 3;
    if(a.kind==WC_REG && a.width<=2 && (b.kind==WC_INDIRECT || b.kind==WC_INDEXED))return b.kind==WC_INDEXED?4:3;
    if(b.kind==WC_REG && b.width<=2 && (a.kind==WC_INDIRECT || a.kind==WC_INDEXED))return a.kind==WC_INDEXED?4:3;
    return 0;
  }
  if(WC_OP("add") || WC_OP("sub") || WC_OP("cmp") || WC_OP("anl") || WC_OP("orl") || WC_OP("xrl")) {
    int logic=WC_OP("anl") || WC_OP("orl") || WC_OP("xrl");
    if(a.kind!=WC_REG || (logic && a.width==4))return 0;
    if(b.kind==WC_REG && a.width==b.width)return 2;
    if(b.kind==WC_IMM)return a.width>1?4:((WC_OP("add") || logic) && a.reg==11?2:3);
    return 0;
  }
#undef WC_OP
  return 0;
}

static wide_byte_cost
wide_function_bytes_context(const char *text,size_t length,wide_cost_resolver resolve,const void *context)
{
  wide_byte_cost cost={0,0};size_t at=0;
  while(at<length) {
    size_t end=at;while(end<length && text[end]!='\n')++end;
    const char *p=text+at,*last=text+end;
    while(p<last && isspace((unsigned char)*p))++p;
    if(p<last && *p!=';') {
      const char *q=p;while(q<last && *q!=';' && !isspace((unsigned char)*q))++q;
      if(q>p && q[-1]==':') {while(q<last && isspace((unsigned char)*q))++q;if(q<last && *q!=';')++cost.unknown;}
      else {unsigned bytes=wide_instruction_bytes_context(p,(size_t)(last-p),resolve,context);if(!bytes || cost.bytes>UINT_MAX-bytes)++cost.unknown;else cost.bytes+=bytes;}
    }
    at=end+1;
  }
  return cost;
}
static inline unsigned wide_instruction_bytes(const char *text,size_t length)
{return wide_instruction_bytes_context(text,length,NULL,NULL);}
static inline wide_byte_cost wide_function_bytes(const char *text,size_t length)
{return wide_function_bytes_context(text,length,NULL,NULL);}
#endif
