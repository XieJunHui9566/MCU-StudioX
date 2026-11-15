#include "register_facts.h"
#include "instruction_effects.h"
/* SPDX-License-Identifier: GPL-2.0-or-later
 * 251 原生宽度后端：整数运算、浮点值传递及寄存器 ABI 的直接调用。
 * 宽值、寄存器重叠、控制流与溢出槽在发射前统一决定。 */
#include "common.h"
#include "wide.h"
#include "dbuf_string.h"
#include <stdint.h>
#include <stdarg.h>

extern struct dbuf_s *codeOutBuf;
#include "wide_leaf_effects.c.inc"
#ifdef MX251_WIDE_AUDIT
#include "wide_audit.h"
#else
#define WA_FAIL(stage) 0
#endif
enum { WV_MAX = 128, WN_MAX = 512, WO_MAX = 32, WA_MAX = 8, WJ_MAX = 2048, WB_WORDS = WV_MAX / 64, WM_LOAD=0x10000, WM_STORE, WM_ADDRESS, WM_UMULW, WM_NOP, WM_PARAM_LOAD, WM_PARAM_STORE, WM_PARAM_ADDRESS, WM_OBJECT_LOAD, WM_OBJECT_STORE, WM_SYMBOL_LOAD, WM_SYMBOL_STORE, WM_BIT_LOAD, WM_BIT_STORE, WM_BIT_CAST };
typedef struct { uint64_t w[WB_WORDS]; } wide_bits;
typedef struct {
  symbol *sym;
  int width, reg, slot, pin, preference, weight, reads;
  bool sign, pointer, codePointer, floating, symbolSnapshot;
  wide_bits edges;
} wide_value;
typedef struct { int value, width; uint32_t literal; bool sign, floating, codePointer; } wide_operand;
/* 可取址对象有独占的完整存储期，不与虚拟寄存器的溢出槽合并。
 * 不复用旧后端尚未重新布局的 symbol.stack，也不缓存对象内容。 */
typedef struct { symbol *sym; int slot, size; } wide_object;
typedef struct {
  unsigned op;
  iCode *origin;
  wide_operand left, right, addressIndex;
  symbol *callee;
  symbol *addressSymbol;
  int addressOffset, addressObject;
  unsigned addressOp;
  bool byteAccess, codeAccess, pointerAccess;
  unsigned args, stackArgs, stackBytes;
  wide_operand stackOperands[WA_MAX-1];
  unsigned char stackWidths[WA_MAX-1];
  bool ordinaryCall;
  uint32_t callSaveOmit;
  signed char multiplyDigits[32];
  int multiplyTop;
  int result, label, target, succ[2], width;
  int jumpStart, jumpCount;
  bool sign, floating, trueBranch;
  wide_bits use, def, in, out;
} wide_node;
/* 文本位置用于替换，字节上界用于证明距离；二者不能混用。 */
typedef struct { int label;size_t text;unsigned upper,barrier; } wide_label_site;
typedef struct {
  int target;const char *condition;size_t begin,end;
  unsigned upperBegin,upperEnd,barrier,weight;
} wide_branch_site;
typedef struct {
  wide_label_site labels[WN_MAX*4];wide_branch_site branches[WN_MAX];
  unsigned labelCount,branchCount,emitUpper,emitBarrier;
  bool relaxDisabled;
  wide_value values[WV_MAX]; wide_node nodes[WN_MAX];
  wide_object objects[WO_MAX];
  /* 独立的有界边池保留表项次序和重复目标；整体复制分配候选时没有悬空指针。 */
  int jumpLabels[WJ_MAX], jumpSuccessors[WJ_MAX], nj;
  int nv, nn, no, frame, serial, received, stackDepth, foldedCasts, reusedParameterLoads;
  unsigned emitWeight, emitCost, emitInstructions;
  bool body, sampledQualified, formalAddress, ordinaryEntry, allocationInvalid;
  bool applyCallEffects,callEffectsInvalid;
  struct dbuf_s *diagnostics, *candidateOutput;
  struct dbuf_s output;
} wide_function;

static bool wb_has (wide_bits b, int n) { return n >= 0 && (b.w[n / 64] & (UINT64_C(1) << (n % 64))); }
static void wb_set (wide_bits *b, int n) { if (n >= 0) b->w[n / 64] |= UINT64_C(1) << (n % 64); }
static bool wb_equal (wide_bits a, wide_bits b) { return !memcmp (&a, &b, sizeof a); }
static wide_bits wb_or (wide_bits a, wide_bits b) { for (int i=0;i<WB_WORDS;++i) a.w[i]|=b.w[i]; return a; }
static wide_bits wb_minus (wide_bits a, wide_bits b) { for (int i=0;i<WB_WORDS;++i) a.w[i]&=~b.w[i]; return a; }

/* 与原后端 __gptrget/put 的平坦寻址一致；高补位仍统一清零。
   近指针/pdata 转换需原后端处理，不能直接当作平坦地址。 */
static bool wide_code_pointer(sym_link *t) {return IS_PTR(t) && DCL_TYPE(t)==CPOINTER && getSize(t)==3 && !IS_FUNC(t->next);}
static bool wide_pointer(sym_link *t) {return IS_PTR(t) && (DCL_TYPE(t)==FPOINTER || DCL_TYPE(t)==GPOINTER || DCL_TYPE(t)==CPOINTER) && getSize(t)==3 && !IS_FUNC(t->next);}
static int wide_width(sym_link *t) {return wide_pointer(t)?4:getSize(t);}
static bool
wide_value_type (sym_link *t, bool sampled)
{
  int size = getSize (t);
  if(isAtomic(t) || (isVolatile(t) && !sampled))return false;
  if(wide_pointer(t))return true;
  if(IS_FLOAT(t))return size==4;
  return IS_SPEC (t) && IS_INTEGRAL (t) &&
    (!IS_BITINT (t) || SPEC_BITINTWIDTH (t) == 8*size) &&
    (size == 1 || size == 2 || size == 4);
}
/* CODE 只扩展函数内部的取址/读取，公开 ABI 仍只接纳原有平坦数据指针。 */
static bool wide_type(sym_link *t) {return wide_value_type(t,false) && !wide_code_pointer(t) && !IS_BIT(t) && !IS_BOOL(t);}

/* ABI4 前两个参数在 DR4/DR12；其余标量按声明宽度、逆序压栈。
 * 平坦指针在 MIR 中为四字节，栈上仍严格占三字节，不改变公开 ABI。 */
static unsigned wide_register_count(sym_link *type) {return FUNC_SDCCCALL(type)==2?1:2;}

static bool
wide_stack_signature(sym_link *type)
{
  unsigned index=0;
  if(IFFUNC_ISSMALLC(type) || IFFUNC_ISDYNAMICC(type))return false;
  for(value *arg=FUNC_ARGS(type);arg;arg=arg->next,++index)
    if(index==WA_MAX || (index>=wide_register_count(type) && !wide_type(arg->type)))return false;
  return true;
}

static bool
wide_operand_from (wide_function *f, operand *op, wide_operand *v)
{
  sym_link *t;
  v->value = -1;
  /* 前端联合体读操作会把 volatile 限定带到结果临时值。
   * 非取址 ITEMP 表示已经取样的值，其寄存器使用不再次访问内存；
   * 经类型提升带上该限定的字面量同样不表示一次内存访问。
   * 实际读写仍只能经 WM_LOAD/WM_STORE 导入，保留限定和字节次序；
   * 真实变量、取址临时值与 atomic 不能借此放宽，原 iCode 类型不修改。 */
  bool sampled=op && (IS_OP_LITERAL(op) ||
    (IS_ITEMP(op) && !IS_OP_GLOBAL(op) && !OP_SYMBOL(op)->addrtaken));
  if (!op || !wide_value_type(t=operandType(op),sampled) ||
      (!sampled && isOperandVolatile(op,FALSE))) return false;
  if(sampled && isVolatile(t))f->sampledQualified=true;
  v->width = wide_width (t); v->floating=IS_FLOAT(t);v->codePointer=wide_code_pointer(t);
  v->sign = !wide_pointer(t) && !v->floating && !SPEC_USIGN (getSpec (t));
  if (IS_OP_LITERAL (op)) {
    /* 使用编译器已有的 binary32 常量序列化，绝不把浮点数值转成整数。 */
    if(v->floating) {
      v->literal=0;
      for(int i=0;i<4;++i)v->literal|=(uint32_t)byteOfVal(OP_VALUE(op),i)<<(8*i);
    } else v->literal = (uint32_t)(int64_t)operandLitValue (op);
    if (v->width < 4) v->literal &= (UINT32_C(1) << (8*v->width)) - 1;
    if(wide_pointer(t))v->literal&=0xffffff;
    return true;
  }
  /* 初期只接收前端的非逃逸临时值；不把真实内存变量误当虚拟寄存器。 */
  if (!IS_ITEMP (op) || IS_OP_GLOBAL (op) || OP_SYMBOL (op)->addrtaken) return false;
  for (int i=0;i<f->nv;++i) if (!f->values[i].symbolSnapshot && f->values[i].sym == OP_SYMBOL (op)) {
    if(f->values[i].floating!=v->floating || f->values[i].width!=v->width || f->values[i].codePointer!=v->codePointer)return false;
    v->value=i;return true;
  }
  if (f->nv == WV_MAX) return false;
  v->value=f->nv++;
  f->values[v->value]=(wide_value){.sym=OP_SYMBOL(op),.width=v->width,.sign=v->sign,.floating=v->floating,.pointer=wide_pointer(t),.codePointer=v->codePointer,.reg=-1,.slot=-1,.pin=-1,.preference=-1};
  return true;
}

static bool
wide_bit_symbol(operand *op)
{
  if(!op || !IS_TRUE_SYMOP(op) || !IS_BIT(operandType(op)))return false;
  symbol *s=OP_SYMBOL(op);
  return !s->onStack && !s->_isparm && !isAtomic(operandType(op)) &&
    IN_BITSPACE(SPEC_OCLS(s->etype));
}

static bool
wide_register_parameter(symbol *s)
{
  /* 普通 ABI2 的寄存器首参也可能直接保留为真实栈符号，例如显式
   * __reentrant 而未启用全局 stack-auto，或指向 volatile 的指针。
   * 为它复用独占物理 home 与逐次快照，不能把真实变量注册成临时值。
   * 参数本体的 volatile/atomic 限定仍由 wide_type 拒绝；所指对象的
   * 限定继续由内存节点处理。显式试验开关也允许 ABI4 的两个真实
   * 寄存器形参；按 FUNC_ARGS 的符号身份确认位置，不猜测名称或槽。 */
  if(!s->_isparm || !s->onStack ||
     (!s->addrtaken && FUNC_SDCCCALL(currFunc->type)!=2 &&
      !(FUNC_SDCCCALL(currFunc->type)==4 && mcs251WideOrdinaryEnabled)) ||
     !wide_type(s->type))return false;
  int index=0;
  for(value *arg=FUNC_ARGS(currFunc->type);arg && index<wide_register_count(currFunc->type);arg=arg->next,++index)
    if(arg->sym==s)return true;
  return false;
}

static bool
wide_parameter_slot(operand *op,sym_link **type,int *offset)
{
  if(!op || !IS_TRUE_SYMOP(op))return false;
  symbol *s=OP_SYMBOL(op);sym_link *t=operandType(op);
  if(!s->_isparm || !s->onStack ||
     isOperandVolatile(op,TRUE) || !wide_type(t))return false;
  int at=-2,index=0;
  /* ECALL 返回地址占三字节；偏移相对于入口 SPX，独立于旧后端帧重排。
   * 取址对象复用原物理栈槽；volatile/atomic 形参仍回退。 */
  for(value *arg=FUNC_ARGS(currFunc->type);arg;arg=arg->next,++index) {
    if(index<wide_register_count(currFunc->type))continue;
    at-=getSize(arg->type);
    if(arg->sym!=s)continue;
    if(!wide_type(arg->type) || getSize(t)!=getSize(arg->type) ||
       IS_FLOAT(t)!=IS_FLOAT(arg->type) || wide_pointer(t)!=wide_pointer(arg->type))return false;
    *type=arg->type;*offset=at;return true;
  }
  return false;
}

static bool wide_stack_object(wide_function *f,symbol *s,int *object);

static bool
wide_symbol_snapshot(wide_function *f,wide_node *n,operand *op,bool store,wide_operand *v)
{
  if(!op || !IS_TRUE_SYMOP(op))return false;
  symbol *s=OP_SYMBOL(op);sym_link *t=operandType(op);memmap *m=SPEC_OCLS(s->etype);
  /* 位读取在原位置取样为规范的 0/1 字节。位写入另行处理，必须检查
   * 完整右值非零，不能先截成一个字节（例如 0x100 仍必须写入 1）。 */
  if(!store && wide_bit_symbol(op)) {
    if(f->nv==WV_MAX || f->nn==WN_MAX)return false;
    int value=f->nv++;
    f->values[value]=(wide_value){.sym=s,.width=1,.symbolSnapshot=true,
      .reg=-1,.slot=-1,.pin=-1,.preference=-1};
    *v=(wide_operand){.value=value,.width=1};
    wide_node *memory=&f->nodes[f->nn++];
    *memory=(wide_node){.op=WM_BIT_LOAD,.origin=n->origin,.result=value,.width=1,
      .addressSymbol=s,.addressObject=-1,.label=-1,.target=-1,.succ={-1,-1},
      .left={.value=-1},.right={.value=-1},.addressIndex={.value=-1},.byteAccess=true};
    wb_set(&memory->def,value);return true;
  }
  if(s->onStack && (!s->_isparm || wide_register_parameter(s)) &&
     ((IS_SPEC(s->type) && IS_SPEC(t)) ||
      (wide_pointer(s->type) && wide_pointer(t) && !wide_code_pointer(t) && !wide_code_pointer(s->type))) &&
     !IS_BITVAR(t) && !IS_BOOL(t) && wide_value_type(t,true) &&
     getSize(t)==getSize(s->type)) {
    int object,width=wide_width(t);
    if(f->nv==WV_MAX || f->nn==WN_MAX || !wide_stack_object(f,s,&object))return false;
    int value=f->nv++;bool floating=IS_FLOAT(t),pointer=wide_pointer(t);
    bool sign=!floating && !pointer && !SPEC_USIGN(getSpec(t));
    /* 直接符号访问和 &local 必须复用同一个对象槽。每次读取重新取样，
     * 不跨别名写入、调用或分支缓存；volatile 保持逐字节访问次序。 */
    f->values[value]=(wide_value){.sym=s,.width=width,.sign=sign,.floating=floating,.pointer=pointer,
      .symbolSnapshot=true,.reg=-1,.slot=-1,.pin=-1,.preference=-1};
    *v=(wide_operand){.value=value,.width=width,.sign=sign,.floating=floating};
    wide_node *memory=&f->nodes[f->nn++];
    *memory=(wide_node){.op=store?WM_OBJECT_STORE:WM_OBJECT_LOAD,.origin=n->origin,
      .result=store?-1:value,.width=width,.sign=sign,.floating=floating,
      .addressSymbol=s,.addressObject=object,.pointerAccess=pointer,.label=-1,.target=-1,.succ={-1,-1},
      .left={.value=-1},.right={.value=-1},.addressIndex={.value=-1},
      .byteAccess=isVolatile(t) || isOperandVolatile(op,TRUE)};
    if(store){memory->right=*v;wb_set(&memory->use,value);}
    else wb_set(&memory->def,value);
    return true;
  }
  sym_link *parameterType;int parameterOffset;
  if(wide_parameter_slot(op,&parameterType,&parameterOffset)) {
    if(f->nv==WV_MAX || f->nn==WN_MAX)return false;
    int value=f->nv++,width=wide_width(parameterType);
    bool floating=IS_FLOAT(parameterType),pointer=wide_pointer(parameterType);
    bool sign=!floating && !pointer && !SPEC_USIGN(getSpec(parameterType));
    /* 每次读写均生成独立内存节点；写回不改变调用方的副本布局，
     * 三字节指针的 MIR 填充字节不能落到邻近参数。 */
    f->values[value]=(wide_value){.sym=s,.width=width,.sign=sign,.pointer=pointer,
      .floating=floating,.symbolSnapshot=true,.reg=-1,.slot=-1,.pin=-1,.preference=-1};
    *v=(wide_operand){.value=value,.width=width,.sign=sign,.floating=floating};
    wide_node *memory=&f->nodes[f->nn++];
    *memory=(wide_node){.op=store?WM_PARAM_STORE:WM_PARAM_LOAD,.origin=n->origin,
      .result=store?-1:value,.width=width,.sign=sign,.floating=floating,
      .addressOffset=parameterOffset,.addressObject=-1,.label=-1,.target=-1,.succ={-1,-1},
      .left={.value=-1,.width=getSize(parameterType),.sign=sign},
      .right={.value=-1},.addressIndex={.value=-1}};
    if(store){memory->right=*v;wb_set(&memory->use,value);}
    else wb_set(&memory->def,value);
    return true;
  }
  int width=getSize(t);
  /* 真实对象每次都读写内存，不能按 symbol 合并为长期寄存器值。
   * 先只接纳直接 RAM 标量及单字节 SFR；位空间、分页、CODE、远对象、
   * 指针、形参/栈对象及 atomic 均保持原来的整函数回退。 */
  if(s->onStack || s->_isparm || !IS_SPEC(t) || IS_BITVAR(t) || IS_BOOL(t) ||
     !wide_value_type(t,true) || !IN_DIRSPACE(m) || IN_BITSPACE(m) ||
     IN_CODESPACE(m) || IN_FARSPACE(m) || IN_PAGEDSPACE(m) ||
     (m==sfr && width!=1) || (IN_REGSP(m) && m!=sfr) ||
     f->nv==WV_MAX || f->nn==WN_MAX)return false;
  int value=f->nv++;
  bool floating=IS_FLOAT(t),sign=!floating && !SPEC_USIGN(getSpec(t));
  f->values[value]=(wide_value){.sym=s,.width=width,.sign=sign,.floating=floating,
    .symbolSnapshot=true,.reg=-1,.slot=-1,.pin=-1,.preference=-1};
  *v=(wide_operand){.value=value,.width=width,.sign=sign,.floating=floating};
  wide_node *memory=&f->nodes[f->nn++];
  *memory=(wide_node){.op=store?WM_SYMBOL_STORE:WM_SYMBOL_LOAD,.origin=n->origin,
    .result=store?-1:value,.width=width,.sign=sign,.floating=floating,
    .addressSymbol=s,.addressObject=-1,.label=-1,.target=-1,.succ={-1,-1},
    .left={.value=-1},.right={.value=-1},.addressIndex={.value=-1},
    .byteAccess=isVolatile(t) || isOperandVolatile(op,TRUE) || m==sfr};
  if(store){memory->right=*v;wb_set(&memory->use,value);}
  else wb_set(&memory->def,value);
  return true;
}

static bool
wide_result (wide_function *f, wide_node *n, operand *op)
{
  wide_operand v;
  if(op && IS_TRUE_SYMOP(op)) {
    if(!wide_symbol_snapshot(f,n,op,true,&v))return false;
  } else if (!wide_operand_from(f,op,&v) || v.value < 0) return false;
  n->result=v.value;wb_set(&n->def,v.value);
  n->width=v.width;n->sign=v.sign;n->floating=v.floating;
  return true;
}

static bool
wide_use (wide_function *f, wide_node *n, operand *op, wide_operand *v)
{
  if(op && IS_TRUE_SYMOP(op)) {
    /* SEND/IPUSH 延迟到 CALL 的原策略只允许纯值。不能将真实对象读取
     * 偷移到调用点；前端已显式取样为 ITEMP 的参数仍按原路径处理。 */
    if(n->op==CALL || !wide_symbol_snapshot(f,n,op,false,v))return false;
  } else if (!wide_operand_from(f,op,v)) return false;
  if (v->value >= 0) {wb_set(&n->use,v->value);++f->values[v->value].reads;f->values[v->value].weight+=1+4*n->origin->depth;}
  return true;
}

static bool
wide_memory(wide_function *f,wide_node *n,iCode *ic,bool store)
{
  operand *p=store?IC_RESULT(ic):IC_LEFT(ic),*value=store?IC_RIGHT(ic):IC_RESULT(ic);
  sym_link *pt=operandType(p),*vt=operandType(value);
  /* 不把只读 CODE 存储退化成 XDATA 写，也不猜测运行期的存储空间。 */
  if(store && wide_code_pointer(pt))return false;
  /* 指针对象物理占三字节，MIR 值占四字节；单独标记访问，
   * 不能用宽寄存器大小读写内存。CODE 指针值及函数/近指针保留回退。 */
  bool pointer=wide_pointer(pt) && wide_pointer(pt->next) && wide_pointer(vt) &&
    !wide_code_pointer(pt) && !wide_code_pointer(pt->next) && !wide_code_pointer(vt);
  if(!wide_pointer(pt) || isAtomic(pt->next) || isAtomic(vt))return false;
  if(!pointer && (!IS_SPEC(pt->next) || (!IS_INTEGRAL(pt->next) && !IS_FLOAT(pt->next)) ||
     IS_BITVAR(pt->next) || !IS_SPEC(vt) || (!IS_INTEGRAL(vt) && !IS_FLOAT(vt)) ||
     IS_FLOAT(pt->next)!=IS_FLOAT(vt) || getSize(pt->next)!=getSize(vt) ||
     (getSize(vt)!=1 && getSize(vt)!=2 && getSize(vt)!=4) ||
     (IS_BITINT(pt->next) && SPEC_BITINTWIDTH(pt->next)!=8*getSize(vt))))return false;
  if(!store && IC_RIGHT(ic) && (!IS_OP_LITERAL(IC_RIGHT(ic)) || operandLitValue(IC_RIGHT(ic))!=0))return false;
  if(!wide_use(f,n,p,&n->left))return false;
  n->op=store?WM_STORE:WM_LOAD;n->width=wide_width(vt);n->codeAccess=wide_code_pointer(pt);n->pointerAccess=pointer;
  /* 易失访问保持原后端的字节宽度、地址递增顺序；普通对象才使用字读写。 */
  n->byteAccess=isVolatile(pt->next) || isOperandVolatile(value,TRUE);
  if(store) {if(!wide_use(f,n,value,&n->right))return false;}
  else if(!wide_result(f,n,value))return false;
  return true;
}

static bool
wide_stack_object (wide_function *f, symbol *s, int *object)
{
  int size=getSize(s->type);
  /* 寄存器真实符号用本地槽接收；栈形参继续使用调用方物理槽，不复制。
   * 静态/零长/过大对象保持整函数回退。 */
  if(!s->onStack || (s->_isparm && !wide_register_parameter(s)) || !s->level || IS_STATIC(s->etype) ||
     size<=0 || size>512)return false;
  for(int i=0;i<f->no;++i)if(f->objects[i].sym==s) {*object=i;return true;}
  if(f->no==WO_MAX || size>512-f->frame)return false;
  *object=f->no++;
  f->objects[*object]=(wide_object){.sym=s,.slot=f->frame,.size=size};
  f->frame+=size;
  return true;
}

static bool
wide_parameter_load(wide_function *f,wide_node *n,iCode *ic)
{
  sym_link *t;int offset;
  if(!wide_parameter_slot(IC_RIGHT(ic),&t,&offset) ||
     (ic->op==CAST && (IS_BOOL(operandType(IC_RESULT(ic))) || IS_BIT(operandType(IC_RESULT(ic))))) ||
     !wide_result(f,n,IC_RESULT(ic)) || n->floating!=IS_FLOAT(t) ||
     (ic->op=='=' && n->width!=wide_width(t)) ||
     (wide_pointer(t) && f->values[n->result].codePointer))return false;
  n->op=WM_PARAM_LOAD;n->addressOffset=offset;
  n->left.width=getSize(t);n->left.sign=!wide_pointer(t) && !n->floating && !SPEC_USIGN(getSpec(t));return true;
}

static bool
wide_pointer_offset(iCode *ic,wide_operand *v)
{
  operand *op=IC_RIGHT(ic);
  if((ic->op!='+' && ic->op!='-') || !wide_pointer(operandType(IC_RESULT(ic))) ||
     !wide_pointer(operandType(IC_LEFT(ic))) || !IS_OP_LITERAL(op) ||
     !IS_INTEGRAL(operandType(op)) || getSize(operandType(op))!=8)return false;
  uint64_t bits=operandLitValueUll(op);
  bool negative=!SPEC_USIGN(getSpec(operandType(op))) && (bits&UINT64_C(0x8000000000000000));
  /* 前端结构字段偏移使用 long long。仅精确可表示的常量进入地址运算，
   * 不截断一般 64 位值，不经 double 转换，也不开放 64 位整数算术。 */
  if(negative ? bits<UINT64_C(0xffffffff80000000) : bits>UINT64_C(0xffffffff))return false;
  *v=(wide_operand){.value=-1,.width=4,.literal=(uint32_t)bits,.sign=negative};return true;
}

static bool
wide_import (wide_function *f, ebbIndex *ebbi)
{
  operand *pending[2]={NULL,NULL};
  unsigned pendingMask=0,pushedCount=0;
  operand *pushed[WA_MAX-1]={NULL};
  for (int b=0;b<ebbi->count;++b) {
    eBBlock *bb=ebbi->bbOrder[b];
    if (!bb->sch) continue;
    if (bb->noPath && optimize.label4 && bb->entryLabel != entryLabel && bb->entryLabel != returnLabel) continue;
    for (iCode *ic=bb->sch;ic;ic=ic==bb->ech?NULL:ic->next) {
      if (f->nn==WN_MAX) return false;
      int first=f->nn;
      wide_node *n=&f->nodes[f->nn++];
      n->op=ic->op;n->origin=ic;n->result=-1;n->left.value=n->right.value=n->addressIndex.value=-1;
      n->target=n->label=-1;n->succ[0]=n->succ[1]=-1;n->addressObject=-1;
      /* SEND 只收集参数；在 CALL 处并行复制，避免相互覆盖。 */
      if((pendingMask || pushedCount) && ic->op!=SEND && ic->op!=IPUSH && ic->op!=CALL) return false;
      switch (ic->op) {
      case FUNCTION: case ENDFUNCTION: break;
      case LABEL: n->label=IC_LABEL(ic)->key;break;
      case GOTO: n->target=IC_LABEL(ic)->key;break;
      case RECEIVE:
        if (f->body || ic->argreg<1 || ic->argreg>wide_register_count(currFunc->type) ||
            !wide_result(f,n,IC_RESULT(ic))) return false;
        /* 普通首参在 DPL/DPH/B/A，发射时先快照再写已分配宽值。
         * 不能像 ABI4 一样把 RECEIVE 固定到 DR4/DR12。 */
        if(!f->ordinaryEntry)f->values[n->result].pin=(ic->argreg==1?8:16)-n->width;
        ++f->received;
        break;
      case RETURN:
        if(IC_LEFT(ic)) {
          if (!wide_use(f,n,IC_LEFT(ic),&n->left)) return false;
          if(n->left.floating!=IS_FLOAT(currFunc->type->next) || n->left.width!=wide_width(currFunc->type->next))return false;
          n->width=n->left.width;
        } else if(!IS_VOID(currFunc->type->next)) return false;
        if(n->left.value>=0) f->values[n->left.value].preference=8-n->width;
        f->body=true;break;
      case IPUSH:
        /* 仅推迟连续的纯值参数；遇到计算/存储/控制流/嵌套调用就整函数回退，
         * 不把旧时刻的内存读取移动到 CALL。CALL 统一记录所有输入的 use。 */
        if(!ic->parmPush || pushedCount==WA_MAX-1)return false;
        pushed[pushedCount++]=IC_LEFT(ic);n->op=WM_NOP;f->body=true;break;
      case SEND:
        if(ic->argreg<1 || ic->argreg>2 || (pendingMask&(1u<<(ic->argreg-1)))) return false;
        pending[ic->argreg-1]=IC_LEFT(ic);pendingMask|=1u<<(ic->argreg-1);
        f->body=true;break;
      case CALL: {
        /* 普通入口复用同一调用保存与物理栈编组；CALL 的 ABI、可重入、
         * 类型与 cdef 门槛仍逐项验证。跨普通入口的尾跳转仍禁用，
         * 确保返回值转换和本帧释放由当前函数完成。 */
        sym_link *t=operandType(IC_LEFT(ic));
        if(!IS_SYMOP(IC_LEFT(ic)) || !IS_FUNC(t) || (FUNC_SDCCCALL(t)!=4 && FUNC_SDCCCALL(t)!=2) ||
           !IFFUNC_ISREENT(t) || IFFUNC_ISISR(t) || IFFUNC_ISNAKED(t) ||
           IFFUNC_ISCRITICAL(t) || IFFUNC_CALLEESAVES(t) || IFFUNC_ISBANKEDCALL(t) ||
           FUNC_REGBANK(t) || IFFUNC_HASVARARGS(t) || IFFUNC_ISNORETURN(t) ||
           (!wide_type(t->next) && !IS_VOID(t->next)) || !wide_stack_signature(t)) return false;
        /* 普通可重入 ABI 仅第一个标量在 DPL/DPH/B/A，其余按原宽度压栈。
         * 非可重入静态形参、间接调用及其他调用约定仍整体回退。 */
        n->ordinaryCall=FUNC_SDCCCALL(t)==2;
        /* 编译器生成的旧运行库调用有独立的库配置与边界验收。
         * 本次只迁移显式普通函数调用，不按函数名猜测库 ABI。 */
        if(n->ordinaryCall && OP_SYMBOL(IC_LEFT(ic))->cdef)return false;
        unsigned registers=n->ordinaryCall?1:2,count=0,bytes=0;
        for(value *arg=FUNC_ARGS(t);arg;arg=arg->next) {
          if(count==WA_MAX || !wide_type(arg->type)) return false;
          wide_operand *o;
          operand *input;
          if(count<registers) {o=count?&n->right:&n->left;input=pending[count];}
          else {
            if(count-registers>=pushedCount)return false;
            o=&n->stackOperands[pushedCount-1-(count-registers)];input=pushed[pushedCount-1-(count-registers)];
            n->stackWidths[pushedCount-1-(count-registers)]=getSize(arg->type);
            bytes+=getSize(arg->type);
          }
          if(!input || !wide_use(f,n,input,o) || o->width!=wide_width(arg->type) || o->floating!=IS_FLOAT(arg->type)) return false;
          ++count;
        }
        if(pendingMask!=((1u<<(count<registers?count:registers))-1) ||
           pushedCount!=(count>registers?count-registers:0) || bytes!=ic->parmBytes)return false;
        n->stackArgs=pushedCount;n->stackBytes=bytes;
        n->args=count;n->callee=OP_SYMBOL(IC_LEFT(ic));
        if(IC_RESULT(ic) && !IS_VOID(t->next) &&
           (!wide_result(f,n,IC_RESULT(ic)) || n->width!=wide_width(t->next) || n->floating!=IS_FLOAT(t->next))) return false;
        pending[0]=pending[1]=NULL;pendingMask=pushedCount=0;f->body=true;break;
      }
      case JUMPTABLE: {
        int count=elementsInSet(IC_JTLABELS(ic));
        /* 前端先做原宽度的上下界比较和最小值归一化，随后索引为 0..255。
         * 不删除这些检查；超出边池容量时整函数回退，不截断表项。 */
        if(count<=0 || count>256 || count>WJ_MAX-f->nj ||
           !wide_use(f,n,IC_JTCOND(ic),&n->left) || n->left.floating ||
           wide_pointer(operandType(IC_JTCOND(ic))))return false;
        n->jumpStart=f->nj;n->jumpCount=count;
        /* 只读遍历；setFirst/NextItem 会改动共享集合的 curr 游标。 */
        for(const set *entry=IC_JTLABELS(ic);entry;entry=entry->next)
          f->jumpLabels[f->nj++]=((const symbol *)entry->item)->key;
        if(f->nj!=n->jumpStart+count)return false;
        f->body=true;break;
      }
      case IFX:
        if (!wide_use(f,n,IC_COND(ic),&n->left) || (!IC_TRUE(ic) && !IC_FALSE(ic))) return false;
        /* -0.0f 不能用整数非零判断；未降为支持调用的浮点语义整体回退。 */
        if(n->left.floating)return false;
        n->target=(IC_TRUE(ic)?IC_TRUE(ic):IC_FALSE(ic))->key;n->trueBranch=IC_TRUE(ic)!=NULL;
        n->width=n->left.width;f->body=true;break;
      case '=': case CAST:
        if(wide_parameter_load(f,n,ic)) {f->body=true;break;}
        if(POINTER_SET(ic)) {if(!wide_memory(f,n,ic,true))return false;f->body=true;break;}
        if(wide_bit_symbol(IC_RESULT(ic))) {
          if(!wide_use(f,n,IC_RIGHT(ic),&n->right) || n->right.floating ||
             !IS_INTEGRAL(operandType(IC_RIGHT(ic))))return false;
          n->op=WM_BIT_STORE;n->addressSymbol=OP_SYMBOL(IC_RESULT(ic));
          f->body=true;break;
        }
        if(ic->op==CAST && IS_BIT(operandType(IC_RESULT(ic)))) {
          /* 位临时值必须规范化为 0/1；不能先截为字节，否则 0x100 会变成假。
           * 真实位对象的访问仍由独立节点完成，不缓存或合并 volatile 访问。 */
          if(!IS_ITEMP(IC_RESULT(ic)) || !wide_result(f,n,IC_RESULT(ic)) || n->width!=1 ||
             !wide_use(f,n,IC_RIGHT(ic),&n->right) || n->right.floating ||
             !IS_INTEGRAL(operandType(IC_RIGHT(ic))))return false;
          n->op=WM_BIT_CAST;f->body=true;break;
        }
        if (ic->op==CAST && IS_BOOL(operandType(IC_RESULT(ic)))) return false;
        if (POINTER_SET(ic) || !wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_RIGHT(ic),&n->right)) return false;
        if(n->floating!=n->right.floating)return false;
        /* CODE 与数据指针的直接转换保留旧路径；整数到明确类型指针的
         * 转换按原有 24 位截断规则规范化，不能丢失指针的读取空间。 */
        if(wide_pointer(operandType(IC_RESULT(ic))) && wide_pointer(operandType(IC_RIGHT(ic))) &&
           f->values[n->result].codePointer!=n->right.codePointer)return false;
        if (ic->op=='=' && n->width!=n->right.width) return false;
        f->body=true;break;
      case GET_VALUE_AT_ADDRESS:
        if(!wide_memory(f,n,ic,false))return false;f->body=true;break;
      case ADDRESS_OF: {
        if(!wide_pointer(operandType(IC_RESULT(ic))) || !wide_result(f,n,IC_RESULT(ic)) ||
           !IS_TRUE_SYMOP(IC_LEFT(ic)) || !IC_RIGHT(ic) || !IS_OP_LITERAL(IC_RIGHT(ic)))return false;
        symbol *s=OP_SYMBOL(IC_LEFT(ic));memmap *m=SPEC_OCLS(s->etype);
        bool code=wide_code_pointer(operandType(IC_RESULT(ic)));
        if(s->onStack) {
          if(code)return false;
          if(operandLitValue(IC_RIGHT(ic))<0 || operandLitValue(IC_RIGHT(ic))>getSize(s->type))return false;
          if(s->_isparm) {
            sym_link *parameterType;int offset;
            f->formalAddress=true;
            if(wide_parameter_slot(IC_LEFT(ic),&parameterType,&offset)) {
              n->op=WM_PARAM_ADDRESS;n->addressSymbol=s;
              n->addressOffset=offset+(int)operandLitValue(IC_RIGHT(ic));f->body=true;break;
            }
          }
          if(!wide_stack_object(f,s,&n->addressObject))return false;
        } else if(IN_CODESPACE(m)) {if(!code)return false;}
        else if(code || !IN_FARSPACE(m) || m->paged)return false;
        n->op=WM_ADDRESS;n->addressSymbol=s;n->addressOffset=(int)operandLitValue(IC_RIGHT(ic));f->body=true;break;
      }
      case '!':
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left)) return false;
        if(n->floating || n->left.floating)return false;
        f->body=true;break;
      case UNARYMINUS:
        /* 无符号 32 位取负是模 2^32 减法；用于 INT_MIN 幅值转换，保留其它宽度回退。 */
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->right) ||
            n->floating || n->right.floating || n->width!=4 || n->sign || n->right.width!=4 || n->right.sign) return false;
        n->op='-';n->left=(wide_operand){.value=-1,.width=4,.literal=0,.sign=false};
        f->body=true;break;
      case GETABIT:
        /* 前端已把单比特掩码转成值选位；只接收范围内的常量位置。
         * 输出为规范化字节 0/1，真实对象的 volatile 快照仍由 wide_use 产生。
         */
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left) ||
            !wide_use(f,n,IC_RIGHT(ic),&n->right) || n->width!=1 || n->sign ||
            n->floating || n->left.floating || n->right.floating ||
            n->right.value>=0 || n->right.literal>=8u*n->left.width ||
            wide_pointer(operandType(IC_LEFT(ic)))) return false;
        f->body=true;break;
      case GETBYTE:
        /* 只选择值中的一个完整字节，不改变产生该值的内存读取。
         * 偏移以最低有效位为零；带符号结果及指针继续交给原后端。
         */
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left) ||
            !wide_use(f,n,IC_RIGHT(ic),&n->right) || n->width!=1 || n->sign ||
            n->floating || n->left.floating || n->right.floating ||
            n->right.value>=0 || n->right.literal%8 ||
            n->right.literal>(unsigned)(8*(n->left.width-1)) ||
            wide_pointer(operandType(IC_LEFT(ic)))) return false;
        f->body=true;break;
      case GETWORD:
        /* 位偏移按最低有效位计；只接受完整落在输入中的常量字节边界。 */
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left) ||
            !wide_use(f,n,IC_RIGHT(ic),&n->right) || n->width!=2 || n->sign ||
            n->floating || n->left.floating || n->right.floating ||
            n->left.width<2 || n->right.value>=0 || n->right.literal%8 ||
            n->right.literal>(unsigned)(8*(n->left.width-2)) ||
            wide_pointer(operandType(IC_LEFT(ic)))) return false;
        f->body=true;break;
      case '*':
        /* 无符号窄因子先零扩展到 WR，MUL 保留完整 32 位积。
         * 新增的 8x8->8 按模 256 取低字节，与前端的截断语义一致；
         * 其他截断宽度、有符号、指针及宽因子仍回退，不扩大未经验证的路径。 */
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left) ||
            !wide_use(f,n,IC_RIGHT(ic),&n->right) || n->sign || n->left.sign || n->right.sign ||
            n->floating || n->left.floating || n->right.floating ||
            (n->width!=1 && n->width!=2 && n->width!=4) || n->left.width>2 || n->right.width>2 ||
            (n->left.width+n->right.width>n->width &&
             !(n->width==1 && n->left.width==1 && n->right.width==1)) ||
            wide_pointer(operandType(IC_RESULT(ic))) || wide_pointer(operandType(IC_LEFT(ic))) ||
            wide_pointer(operandType(IC_RIGHT(ic)))) return false;
        n->op=WM_UMULW;f->body=true;break;
      case '+': case '-': case '^': case '|': case BITWISEAND:
      case LEFT_OP: case RIGHT_OP:
      case '<': case '>': case LE_OP: case GE_OP: case EQ_OP: case NE_OP:
        if (!wide_result(f,n,IC_RESULT(ic)) || !wide_use(f,n,IC_LEFT(ic),&n->left) ||
            (!wide_use(f,n,IC_RIGHT(ic),&n->right) && !wide_pointer_offset(ic,&n->right))) return false;
        /* 浮点算术必须先由前端降为已声明 ABI 的运行库调用。 */
        if(n->floating || n->left.floating || n->right.floating)return false;
        if(!IS_CONDITIONAL(ic) && ic->op!=LEFT_OP && ic->op!=RIGHT_OP && n->right.value<0 && n->right.width<n->width) {
          if(n->right.sign && (n->right.literal&(UINT32_C(1)<<(8*n->right.width-1))))n->right.literal|=~((UINT32_C(1)<<(8*n->right.width))-1);
          if(n->width<4)n->right.literal&=(UINT32_C(1)<<(8*n->width))-1;
          n->right.width=n->width;
        }
        if (ic->op==LEFT_OP || ic->op==RIGHT_OP) {
          if (n->width!=n->left.width) return false;
          if (n->right.value<0 && n->right.literal>=8u*n->width) return false;
        } else if (IS_CONDITIONAL(ic)) {
          if (n->left.width!=n->right.width || n->left.sign!=n->right.sign) return false;
        } else if(n->width!=n->left.width || n->width!=n->right.width) {
          /* 非零起点的 switch 会生成宽输入减常量、字节结果，再接跳转表。
           * 这里只接纳此形状；原宽度范围比较仍在前面。按模 256 保留低字节
           * 与宽减法后截断一致，wload 仍按原输入宽度定位低字，不能改窄类型。 */
          bool jumpNormalize=ic->op=='-' && n->width==1 && !n->sign &&
            n->right.value<0 && n->left.width==n->right.width && n->left.width>1 &&
            !wide_pointer(operandType(IC_LEFT(ic))) && ic->next && ic->next->op==JUMPTABLE &&
            IS_ITEMP(IC_JTCOND(ic->next)) && OP_SYMBOL(IC_JTCOND(ic->next))==OP_SYMBOL(IC_RESULT(ic));
          if(!jumpNormalize && (!f->values[n->result].pointer || (ic->op!='+' && ic->op!='-') || n->left.width!=4 || n->right.width>4))return false;
        }
        f->body=true;break;
      default: return false;
      }
      /* 操作数取样放在原 iCode 前，结果写回放在后。仅重排本条导入时
       * 生成的内存节点，不跨其它 iCode、调用、分支或 volatile 访问。 */
      if(f->nn>first+1) {
        wide_node operation=*n,writeback;bool hasWriteback=false;int at=first;
        for(int k=first+1;k<f->nn;++k) {
          wide_node memory=f->nodes[k];
          if(memory.op==WM_SYMBOL_STORE || memory.op==WM_PARAM_STORE || memory.op==WM_OBJECT_STORE) {if(hasWriteback)return false;writeback=memory;hasWriteback=true;}
          else {if(memory.op!=WM_SYMBOL_LOAD && memory.op!=WM_BIT_LOAD && memory.op!=WM_PARAM_LOAD && memory.op!=WM_OBJECT_LOAD)return false;f->nodes[at++]=memory;}
        }
        f->nodes[at++]=operation;
        if(hasWriteback)f->nodes[at++]=writeback;
        wassert(at==f->nn);
      }
    }
  }
  if (!f->nn || f->received>2 || pendingMask || pushedCount) return false;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    if(n->op!=RETURN && n->op!=ENDFUNCTION && n->op!=GOTO && n->op!=JUMPTABLE && i+1<f->nn) n->succ[0]=i+1;
    if(n->target>=0) {
      int target=-1;
      for(int j=0;j<f->nn;++j) if(f->nodes[j].label==n->target) {target=j;break;}
      if(target<0) return false;
      n->succ[n->op==GOTO?0:1]=target;
    }
  }
  /* 所有表项都是真实后继。缺失目标必须回退，不能把跳转表当作直落边。 */
  for(int k=0;k<f->nj;++k) {
    int target=-1;
    for(int j=0;j<f->nn;++j)if(f->nodes[j].label==f->jumpLabels[k]) {target=j;break;}
    if(target<0)return false;
    f->jumpSuccessors[k]=target;
  }
  return true;
}

/* 只提升从未取址、非易失且只有入口一次写入的寄存器形参。
 * 整个 MIR 必须只以独占对象节点访问它，不能有地址导出或间接访问。
 * 由此证明所有读取都是同一个入口值；跨调用/分支的活期仍交给原有
 * CFG 活跃分析和调用保存，不对可别名内存作缓存。可修改形参不提升。 */
static void
wide_immutable_parameter_homes(wide_function *f)
{
  if(!f->ordinaryEntry && !mcs251WideOrdinaryEnabled)return;
  bool removed[WO_MAX]={false};int map[WO_MAX],count=0;
  for(int object=0;object<f->no;++object) {
    symbol *s=f->objects[object].sym;
    if(s->addrtaken || !wide_register_parameter(s))continue;
    int initializer=-1,reads=0;bool valid=true;wide_operand input={.value=-1};
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];
      if(n->addressObject!=object)continue;
      if(n->addressSymbol!=s || n->byteAccess || n->addressOffset ||
         n->width!=wide_width(s->type) || n->floating!=IS_FLOAT(s->type) ||
         n->pointerAccess!=wide_pointer(s->type)) {valid=false;break;}
      if(n->op==WM_OBJECT_STORE) {
        if(initializer>=0 || i==0 || n->origin->op!=RECEIVE ||
           n->origin->argreg<1 || n->origin->argreg>wide_register_count(currFunc->type) ||
           f->nodes[i-1].op!=RECEIVE ||
           f->nodes[i-1].origin!=n->origin || n->right.value<0 ||
           f->nodes[i-1].result!=n->right.value) {valid=false;break;}
        initializer=i;input=n->right;
      } else if(n->op==WM_OBJECT_LOAD && initializer>=0)++reads;
      else {valid=false;break;}
    }
    if(!valid || initializer<0 || !reads)continue;
    /* 入口值只能由 RECEIVE 定义；不依赖前端符号名称或旧栈偏移。 */
    for(int i=0;i<f->nn;++i)
      if(i!=initializer-1 && wb_has(f->nodes[i].def,input.value))valid=false;
    if(!valid)continue;
    wide_node *store=&f->nodes[initializer];
    store->op=WM_NOP;store->use=store->def=(wide_bits){{0}};
    store->addressObject=-1;store->addressSymbol=NULL;store->right.value=-1;
    f->values[input.value].reads=0;f->values[input.value].weight=0;
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];
      if(n->addressObject!=object)continue;
      wassert(n->op==WM_OBJECT_LOAD);
      n->op='=';n->right=input;n->use=(wide_bits){{0}};wb_set(&n->use,input.value);
      n->addressObject=-1;n->addressSymbol=NULL;n->pointerAccess=false;
      ++f->values[input.value].reads;f->values[input.value].weight+=1+4*n->origin->depth;
    }
    removed[object]=true;++count;
    dbuf_printf(f->diagnostics,"; wide immutable parameter home removed: %s physical=%d reads=%d\n",s->name,f->objects[object].size,reads);
  }
  if(!count)return;
  int objects=0,frame=0;
  for(int i=0;i<f->no;++i) {
    if(removed[i]) {map[i]=-1;continue;}
    map[i]=objects;wide_object object=f->objects[i];object.slot=frame;
    frame+=object.size;f->objects[objects++]=object;
  }
  for(int i=0;i<f->nn;++i)if(f->nodes[i].addressObject>=0) {
    int object=map[f->nodes[i].addressObject];wassert(object>=0);
    f->nodes[i].addressObject=object;
  }
  f->no=objects;f->frame=frame;
}

static void
wide_reuse_parameter_loads(wide_function *f)
{
  if(!mcs251WideOrdinaryEnabled)return;
  int available[WV_MAX];
  for(int v=0;v<WV_MAX;++v)available[v]=-1;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];bool pure=false;
    /* 只允许跨纯值计算；调用、控制流、取址以及任何其他内存访问均
     * 清空快照。特别是未知别名写入、形参写回和 volatile/SFR 事务，
     * 不能因所读槽相同而沿用旧值。没有跨基本块或调用的内存缓存。 */
    switch(n->op) {
    case WM_PARAM_LOAD:case WM_NOP:case SEND:case '=':case CAST:
    case '+':case '-':case '*':case '^':case '|':case BITWISEAND:
    case LEFT_OP:case RIGHT_OP:case GETABIT:case GETBYTE:case GETWORD:
    case WM_UMULW:case '<':case '>':case LE_OP:case GE_OP:case EQ_OP:case NE_OP:
    case '!':case '~':pure=true;break;
    default:break;
    }
    if(!pure || n->byteAccess) {
      for(int v=0;v<f->nv;++v)available[v]=-1;
      continue;
    }
    /* 被修改的临时值不再代表原来的取样。先失效，避免把对同一 MIR
     * 目标的重新读取错误改成自复制；真实形参存储已在上方作为屏障。 */
    for(int v=0;v<f->nv;++v)if(wb_has(n->def,v))available[v]=-1;
    if(n->op!=WM_PARAM_LOAD || n->result<0)continue;
    int source=-1;
    for(int v=0;v<f->nv;++v)if(available[v]>=0) {
      wide_node *old=&f->nodes[available[v]];
      wide_value *a=&f->values[v],*b=&f->values[n->result];
      /* 同一物理槽、物理读宽及转换结果类型才可复用。保留窄有符号
       * 扩展和三字节指针补位，不将相邻槽或不同 CAST 的读取混合。 */
      if(old->addressOffset==n->addressOffset && old->left.width==n->left.width &&
         old->left.sign==n->left.sign && old->width==n->width &&
         old->sign==n->sign && old->floating==n->floating &&
         a->pointer==b->pointer && a->codePointer==b->codePointer) {source=v;break;}
    }
    if(source<0) {available[n->result]=i;continue;}
    wide_value *value=&f->values[source];
    n->op='=';n->right=(wide_operand){.value=source,.width=value->width,
      .sign=value->sign,.floating=value->floating,.codePointer=value->codePointer};
    n->left=(wide_operand){.value=-1};n->addressOffset=0;
    n->use=(wide_bits){{0}};wb_set(&n->use,source);++f->reusedParameterLoads;
  }
}

static void
wide_strength_reduce (wide_function *f)
{
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    /* 比较编译器支持函数的身份，不能把普通同名用户调用当作乘法。 */
    if(n->op!=CALL || n->args!=2 || n->width!=4 || n->result<0 || n->floating || n->left.floating || n->right.floating ||
       (n->callee!=muldiv[0][2][0] && n->callee!=muldiv[0][2][1]))continue;
    wide_operand input=n->left,constant=n->right;
    if(constant.value>=0) {constant=n->left;input=n->right;}
    if(constant.value>=0 || input.width!=4 || constant.width!=4)continue;
    signed char best[32]={0};int bestCost=100,bestTop=-1;
    for(int form=0;form<2;++form) {
      uint64_t c=constant.literal;signed char digits[32]={0};int top=-1,nonzero=0;
      /* 第二种表示使用有符号数字；第 32 位及以上按模 2^32 丢弃。 */
      for(int k=0;k<32;++k) {
        if(c&1) {
          digits[k]=form && (c&3)==3?-1:1;
          c-=digits[k];top=k;++nonzero;
        }
        c>>=1;
      }
      int cost=top<0?1:top+nonzero-1+(digits[top]<0?2:0);
      if(cost<bestCost) {bestCost=cost;bestTop=top;memcpy(best,digits,sizeof best);}
    }
    /* 限制展开长度，密集常数继续使用运行库，避免无界膨胀。 */
    if(bestCost>7)continue;
    n->op='*';n->left=input;n->right=constant;n->multiplyTop=bestTop;
    memcpy(n->multiplyDigits,best,sizeof best);
  }
}

static void
wide_remove_dead_addresses(wide_function *f)
{
  bool changed;
  do {
    wide_bits used={{0}};changed=false;
    for(int i=0;i<f->nn;++i)used=wb_or(used,f->nodes[i].use);
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];
      if(n->result>=0 && f->values[n->result].pointer && !wb_has(used,n->result) &&
         (n->op==WM_ADDRESS || n->op==CAST || n->op=='=' || n->op=='+' || n->op=='-')) {
        n->op=WM_NOP;n->use=n->def=(wide_bits){{0}};
        n->result=n->left.value=n->right.value=n->addressIndex.value=-1;changed=true;
      }
    }
  } while(changed);
}

static void
wide_stack_addresses (wide_function *f)
{
  int object[WV_MAX],offset[WV_MAX];
  for(int v=0;v<WV_MAX;++v) {object[v]=-1;offset[v]=0;}
  /* 只在同一直线片段传播对象身份；不跨控制流汇合猜测来源。
   * 调用可以修改对象内容，但不能修改未取址的 MIR 指针临时值。
   * 此处不缓存内存值、不消除读写，也不缩短栈对象的存储期。 */
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];int resultObject=-1,resultOffset=0;
    if(n->op==LABEL || n->op==FUNCTION || n->op==ENDFUNCTION ||
       n->op==GOTO || n->op==IFX || n->op==JUMPTABLE || n->op==RETURN)
      for(int v=0;v<f->nv;++v)object[v]=-1;
    if((n->op==WM_LOAD || n->op==WM_STORE) && n->left.value>=0) {
      int v=n->left.value,o=object[v];
      if(o>=0 && offset[v]>=0 && (n->pointerAccess?3:n->width)<=f->objects[o].size-offset[v]) {
        n->addressObject=o;n->addressOffset=offset[v];
        wide_bits address={{0}};wb_set(&address,v);
        n->use=wb_minus(n->use,address);n->left.value=-1;
      }
    }
    if(n->result>=0 && f->values[n->result].pointer) {
      if(n->op==WM_ADDRESS && n->addressObject>=0) {
        resultObject=n->addressObject;resultOffset=n->addressOffset;
      } else if((n->op=='=' || n->op==CAST) && n->right.value>=0 &&
                f->values[n->right.value].pointer && n->right.width==4) {
        resultObject=object[n->right.value];resultOffset=offset[n->right.value];
      } else if((n->op=='+' || n->op=='-') && n->left.value>=0 &&
                object[n->left.value]>=0 && n->right.value<0 && !n->right.floating) {
        int o=object[n->left.value];int64_t delta=n->right.literal;
        if(n->right.sign && (n->right.literal&(UINT32_C(1)<<(8*n->right.width-1))))
          delta-=INT64_C(1)<<(8*n->right.width);
        int64_t position=offset[n->left.value]+(n->op=='+'?delta:-delta);
        /* 允许形成 one-past，只有整个实际访问都落在对象内才折叠寻址。 */
        if(position>=0 && position<=f->objects[o].size) {resultObject=o;resultOffset=(int)position;}
      }
    }
    for(int v=0;v<f->nv;++v)if(wb_has(n->def,v))object[v]=-1;
    if(n->result>=0) {object[n->result]=resultObject;offset[n->result]=resultOffset;}
  }
  /* 仅移除没有任何剩余使用的纯指针计算；调用、内存读写与入口参数保留。
   * 多次定义的值若还有任意使用，不在这里尝试局部死赋值推断。 */
  wide_remove_dead_addresses(f);
}

static void
wide_stack_indexed_bases(wide_function *f)
{
  /* 栈基址重建可减少存活指针/溢出槽，但 SPX 运算未必比保存基址快。
   * 实板速度回归未通过，因此只在明确选择体积优化时启用。 */
  if(!optimize.codeSize)return;
  int definitions[WV_MAX]={0},object[WV_MAX],offset[WV_MAX]={0};
  for(int v=0;v<f->nv;++v)object[v]=-1;
  for(int i=0;i<f->nn;++i)for(int v=0;v<f->nv;++v)
    definitions[v]+=wb_has(f->nodes[i].def,v);
  /* 只识别单一定义、未取址临时值的不可变对象身份，不推断循环 phi。
   * 地址可跨调用/分支存活，但对象内容仍可改变。任何有定义行为的读取
   * 都已执行唯一来源；未执行定义的读取本身不在有效 C 程序的保证内。
   * 从固定对象地址出发迭代，循环依赖不会自行获得已知身份。 */
  bool changed;
  do {
    changed=false;
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];int v=n->result,o=-1;int64_t position=0;
      if(v<0 || definitions[v]!=1 || object[v]>=0 || !f->values[v].pointer ||
         f->values[v].codePointer)continue;
      if(n->op==WM_ADDRESS && n->addressObject>=0 && !n->addressOp) {
        o=n->addressObject;position=n->addressOffset;
      } else if((n->op=='=' || n->op==CAST) && n->right.value>=0 &&
                n->right.width==4 && f->values[n->right.value].pointer) {
        o=object[n->right.value];position=offset[n->right.value];
      } else if((n->op=='+' || n->op=='-') && n->left.value>=0 &&
                n->right.value<0 && !n->right.floating) {
        o=object[n->left.value];int64_t delta=n->right.literal;
        if(n->right.sign && (n->right.literal&(UINT32_C(1)<<(8*n->right.width-1))))
          delta-=INT64_C(1)<<(8*n->right.width);
        position=offset[n->left.value]+(n->op=='+'?delta:-delta);
      }
      if(o>=0 && position>=0 && position<=f->objects[o].size) {
        object[v]=o;offset[v]=(int)position;changed=true;
      }
    }
  } while(changed);
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];int base=n->left.value;
    if((n->op!='+' && n->op!='-') || n->result<0 ||
       !f->values[n->result].pointer || f->values[n->result].codePointer ||
       base<0 || object[base]<0 || n->right.value<0 ||
       n->right.floating || f->values[n->right.value].pointer)continue;
    /* 仅在原运算位置重建基址，动态偏移仍按原宽度/符号扩展并清除
     * 24 位地址的填充位；不移动、合并或缓存任何内存访问。 */
    n->addressObject=object[base];n->addressOffset=offset[base];
    n->addressOp=n->op;n->addressIndex=n->right;n->op=WM_ADDRESS;
    wide_bits removed={{0}};wb_set(&removed,base);n->use=wb_minus(n->use,removed);
    n->left.value=n->right.value=-1;
  }
  wide_remove_dead_addresses(f);
}

static void
wide_fold_indexed_addresses (wide_function *f)
{
  for(int i=0;i<f->nn;++i) {
    wide_node *address=&f->nodes[i];int result=address->result,definitions=0,uses=0;
    if((address->op!='+' && address->op!='-') || result<0 ||
       !f->values[result].pointer || f->values[result].codePointer ||
       address->left.width!=4 || address->left.codePointer || address->right.floating ||
       address->left.value==result || address->right.value==result ||
       (address->right.value>=0 && f->values[address->right.value].pointer))continue;
    for(int k=0;k<f->nn;++k) {
      definitions+=wb_has(f->nodes[k].def,result);
      uses+=wb_has(f->nodes[k].use,result);
    }
    if(definitions!=1 || uses!=1)continue;
    /* 仅把纯地址运算移到同一直线片段中的唯一普通数据访问。
     * 不移动内存读写；来源若重定义或越过控制流/调用，保留原计算。
     * CODE、volatile 与直接栈对象沿用已经验证的独立访问路径。 */
    for(int j=i+1;j<f->nn;++j) {
      wide_node *n=&f->nodes[j];
      if(n->op==LABEL || n->op==GOTO || n->op==IFX || n->op==JUMPTABLE ||
         n->op==CALL || n->op==RETURN || n->op==ENDFUNCTION || n->op==FUNCTION)break;
      if(wb_has(n->use,result)) {
        if((n->op!=WM_LOAD && n->op!=WM_STORE) || n->left.value!=result ||
           n->right.value==result || n->addressOp || n->addressObject>=0 ||
           n->codeAccess || n->byteAccess || n->pointerAccess)break;
        n->addressOp=address->op;n->left=address->left;n->addressIndex=address->right;
        n->use=wb_or(wb_minus(n->use,address->def),address->use);
        address->op=WM_NOP;address->use=address->def=(wide_bits){{0}};
        address->result=address->left.value=address->right.value=-1;
        break;
      }
      if(wb_has(n->def,address->left.value) || wb_has(n->def,address->right.value))break;
    }
  }
}

static void
wide_fold_extensions (wide_function *f)
{
  for(int i=0;i<f->nn;++i) {
    wide_node *cast=&f->nodes[i];
    if(cast->op!=CAST || cast->result<0 || cast->right.value<0 ||
       cast->floating || cast->right.floating || cast->right.sign || cast->width<=cast->right.width ||
       f->values[cast->result].pointer || f->values[cast->right.value].pointer)continue;
    int result=cast->result,definitions=0,uses=0;
    for(int k=0;k<f->nn;++k) {
      wide_node *n=&f->nodes[k];definitions+=wb_has(n->def,result);
      if(wb_has(n->use,result)) {
        uses+=(n->left.value==result)+(n->right.value==result)+(n->addressIndex.value==result);
        for(unsigned k=0;k<n->stackArgs;++k)uses+=n->stackOperands[k].value==result;
      }
    }
    if(definitions!=1 || uses!=1)continue;
    /* 只把无符号零扩展并入同一直线片段的唯一算术使用。
     * 不越过控制流、调用或源值重定义；不移动真实内存/volatile访问。
     */
    for(int j=i+1;j<f->nn;++j) {
      wide_node *n=&f->nodes[j];
      if(n->op==LABEL || n->op==GOTO || n->op==IFX || n->op==JUMPTABLE || n->op==CALL ||
         n->op==RETURN || n->op==FUNCTION || n->op==ENDFUNCTION ||
         n->op==RECEIVE || wb_has(n->def,cast->right.value))break;
      if(!wb_has(n->use,result))continue;
      if(n->width!=cast->width || (n->op!='+' && n->op!='-' && n->op!='^' &&
         n->op!='|' && n->op!=BITWISEAND))break;
      if(n->left.value==result)n->left=cast->right;else n->right=cast->right;
      n->use=wb_minus(n->use,cast->def);wb_set(&n->use,cast->right.value);
      cast->op=WM_NOP;cast->use=cast->def=(wide_bits){{0}};
      cast->result=cast->left.value=cast->right.value=-1;++f->foldedCasts;break;
    }
  }
  /* 删除扩展节点后重算权重；死临时值不能继续影响寄存器优先次序。 */
  for(int i=0;i<f->nv;++i)f->values[i].reads=f->values[i].weight=0;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    int values[WA_MAX+2]={n->left.value,n->right.value,n->addressIndex.value};
    for(unsigned k=0;k<n->stackArgs;++k)values[k+3]=n->stackOperands[k].value;
    for(unsigned k=0;k<3+n->stackArgs;++k)if(wb_has(n->use,values[k])) {
      ++f->values[values[k]].reads;f->values[values[k]].weight+=1+4*n->origin->depth;
    }
  }
}

/* MIR 变换后的候选独立重建活跃信息与分配输入；保留 ABI pin 和对象布局。 */
static void
wide_reset_allocation(wide_function *f)
{
  /* 复制候选不能继承旧图、权重或调用保存结论；位置和 pin 重新交给
   * 原分配器，所有实际内存节点及 CFG 后继原封不动。 */
  for(int v=0;v<f->nv;++v) {
    f->values[v].edges=(wide_bits){{0}};f->values[v].reads=f->values[v].weight=0;
    f->values[v].reg=f->values[v].slot=-1;
  }
  f->frame=0;for(int v=0;v<f->no;++v)f->frame+=f->objects[v].size;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];n->in=n->out=(wide_bits){{0}};n->callSaveOmit=0;
    int values[WA_MAX+2]={n->left.value,n->right.value,n->addressIndex.value};
    for(unsigned k=0;k<n->stackArgs;++k)values[k+3]=n->stackOperands[k].value;
    for(unsigned k=0;k<3+n->stackArgs;++k)if(wb_has(n->use,values[k])) {
      ++f->values[values[k]].reads;f->values[values[k]].weight+=1+4*n->origin->depth;
    }
  }
}

/* 只调度已经取得的无符号整数值，不复制表达式或移动内存取样。
 * 一次定义、一个使用节点且不跨控制流，保证计算仍恰好执行一次；
 * CALL 后重新计算值不能据此重新读取其来源对象或省略调用保存。 */
static bool
wide_sink_integer_node(wide_function *f,wide_node *n)
{
  if((n->op!='+' && n->op!='-' && n->op!='^' && n->op!='|' && n->op!=BITWISEAND) ||
     n->result<0 || n->result>=f->nv || n->floating || n->sign ||
     (n->width!=1 && n->width!=2 && n->width!=4) ||
     n->left.width!=n->width || n->right.width!=n->width ||
     n->left.floating || n->right.floating || n->left.sign || n->right.sign ||
     n->left.codePointer || n->right.codePointer || n->addressIndex.value>=0 ||
     n->args || n->stackArgs || n->addressOp || n->byteAccess || n->codeAccess || n->pointerAccess)return false;
  wide_value *result=&f->values[n->result];
  if(result->width!=n->width || result->pointer || result->codePointer ||
     result->floating || result->sign || result->symbolSnapshot || result->pin>=0)return false;
  int inputs[2]={n->left.value,n->right.value};
  for(int k=0;k<2;++k)if(inputs[k]>=0) {
    if(inputs[k]>=f->nv || inputs[k]==n->result)return false;
    wide_value *value=&f->values[inputs[k]];
    if(value->width!=n->width || value->pointer || value->codePointer || value->floating || value->sign)return false;
  }
  for(int v=0;v<f->nv;++v)
    if(wb_has(n->def,v)!=(v==n->result) ||
       wb_has(n->use,v)!=(v==inputs[0] || v==inputs[1]))return false;
  return true;
}

static bool
wide_sink_block_barrier(wide_node *n)
{
  return n->op==FUNCTION || n->op==ENDFUNCTION || n->op==LABEL ||
    n->op==RECEIVE || n->op==RETURN || n->op==IFX || n->op==GOTO || n->op==JUMPTABLE;
}

static bool
wide_sink_fallthrough(wide_node *n,int index)
{
  return n->op!=RETURN && n->op!=ENDFUNCTION && n->op!=GOTO && n->op!=JUMPTABLE && n->succ[0]==index+1;
}

/* 移动的是节点身份；顺序边必须按新位置重新连接，显式跳转则按身份
 * 映射。只映射旧直落边会跳过被移动的计算，甚至构造错误的循环。 */
static void
wide_sink_move_before(wide_function *f,int from,int before)
{
  int mapped[WN_MAX],original[WN_MAX];
  for(int i=0;i<f->nn;++i)mapped[i]=i;
  mapped[from]=before-1;
  for(int i=from+1;i<before;++i)mapped[i]=i-1;
  for(int i=0;i<f->nn;++i)original[mapped[i]]=i;
  wide_node moving=f->nodes[from];
  memmove(&f->nodes[from],&f->nodes[from+1],(before-from-1)*sizeof f->nodes[0]);
  f->nodes[before-1]=moving;
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];bool fall=wide_sink_fallthrough(n,original[i]);
    for(int k=0;k<2;++k)if(n->succ[k]>=0)
      n->succ[k]=(k==0 && fall)?i+1:mapped[n->succ[k]];
  }
  for(int i=0;i<f->nj;++i)f->jumpSuccessors[i]=mapped[f->jumpSuccessors[i]];
}

static unsigned
wide_sink_integer_expressions(wide_function *f)
{
  unsigned moved=0;
  /* 每个值最多考察一次，顺序固定；不通过复制节点增加代码或扩大搜索。 */
  for(int value=0;value<f->nv;++value) {
    int definition=-1,use=-1,definitions=0,uses=0;
    for(int i=0;i<f->nn;++i) {
      if(wb_has(f->nodes[i].def,value)) {definition=i;++definitions;}
      if(wb_has(f->nodes[i].use,value)) {use=i;++uses;}
    }
    if(definitions!=1 || uses!=1 || use<=definition+1 ||
       !wide_sink_integer_node(f,&f->nodes[definition]))continue;
    wide_node *node=&f->nodes[definition];
    int inputs[2]={node->left.value,node->right.value};unsigned calls=0;bool valid=true;
    for(int i=definition;i<use && valid;++i) {
      wide_node *between=&f->nodes[i];
      if(wide_sink_block_barrier(between) || between->succ[0]!=i+1 ||
         between->succ[1]>=0 || between->jumpCount) {valid=false;break;}
      if(i==definition)continue;
      if(wb_has(between->def,inputs[0]) || wb_has(between->def,inputs[1])) {valid=false;break;}
      calls+=between->op==CALL;
    }
    if(!valid || !calls)continue;
    /* 正常导入的显式边落在 LABEL；这里仍独立检查全部入边，不能把
     * 单一后继误当作单一入口。跳表重复边和回边同样不能越过。 */
    for(int i=0;i<f->nn && valid;++i)for(int k=0;k<2;++k) {
      int to=f->nodes[i].succ[k];
      if(to>=definition && to<=use && !(k==0 && wide_sink_fallthrough(&f->nodes[i],i)))valid=false;
    }
    for(int i=0;i<f->nj;++i)if(f->jumpSuccessors[i]>=definition && f->jumpSuccessors[i]<=use)valid=false;
    if(!valid)continue;
    wide_sink_move_before(f,definition,use);++moved;
    dbuf_printf(f->diagnostics,"; wide integer-sink moved value=%d from=%d before=%d calls=%u\n",value,definition,use,calls);
  }
  if(moved)wide_reset_allocation(f);
  return moved;
}

/* 只合并入口直线前缀中的同地址空间数据指针副本。入口 RECEIVE 的
 * 规范化位模式保持不变；不合并指向的内存，不移动任何读取或写入。 */
static unsigned
wide_entry_pointer_copies(wide_function *f)
{
  unsigned copies=0;
  for(int i=0;i<f->nn;++i) {
    wide_node *copy=&f->nodes[i];
    /* 前缀每个节点只有下一节点一个后继，因此任一后续可达使用必先
     * 经过本节点；不以源代码顺序冒充跨分支的支配关系。 */
    if(copy->succ[0]!=i+1 || copy->succ[1]>=0 || copy->jumpCount)break;
    if((copy->op!='=' && copy->op!=CAST) || copy->result<0 || copy->right.value<0)continue;
    int dst=copy->result,src=copy->right.value;
    if(dst==src)continue;
    wide_value *a=&f->values[src],*b=&f->values[dst];
    if(!a->pointer || !b->pointer || a->codePointer || b->codePointer ||
       a->width!=4 || b->width!=4 || copy->width!=4 || copy->right.width!=4 ||
       a->floating || b->floating || a->sign || b->sign ||
       a->symbolSnapshot || b->symbolSnapshot || b->pin>=0 ||
       !IS_PTR(a->sym->type) || !IS_PTR(b->sym->type) ||
       DCL_TYPE(a->sym->type)!=DCL_TYPE(b->sym->type) ||
       getSize(a->sym->type)!=3 || getSize(b->sym->type)!=3 ||
       isVolatile(a->sym->type) || isVolatile(b->sym->type) ||
       isAtomic(a->sym->type) || isAtomic(b->sym->type))continue;
    int sourceDef=-1,sourceDefs=0,destinationDefs=0;bool valid=true;unsigned uses=0;
    for(int j=0;j<f->nn;++j) {
      wide_node *n=&f->nodes[j];
      if(wb_has(n->def,src)) {sourceDef=j;++sourceDefs;}
      if(wb_has(n->def,dst))++destinationDefs;
      if(!wb_has(n->use,dst))continue;
      /* CALL 的全部寄存器和栈实参都在这些操作数中；未知载体不猜测。 */
      bool represented=n->left.value==dst || n->right.value==dst || n->addressIndex.value==dst;
      for(unsigned k=0;k<n->stackArgs;++k)represented|=n->stackOperands[k].value==dst;
      if(j<=i || !represented)valid=false;
      ++uses;
    }
    if(!valid || !uses || sourceDefs!=1 || destinationDefs!=1 || sourceDef<0 || sourceDef>=i ||
       f->nodes[sourceDef].op!=RECEIVE)continue;
    wide_bits destination={{0}};wb_set(&destination,dst);
    for(int j=0;j<f->nn;++j) {
      wide_node *n=&f->nodes[j];
      if(!wb_has(n->use,dst))continue;
      if(n->left.value==dst)n->left.value=src;
      if(n->right.value==dst)n->right.value=src;
      if(n->addressIndex.value==dst)n->addressIndex.value=src;
      for(unsigned k=0;k<n->stackArgs;++k)if(n->stackOperands[k].value==dst)n->stackOperands[k].value=src;
      n->use=wb_minus(n->use,destination);wb_set(&n->use,src);
    }
    copy->op=WM_NOP;copy->result=copy->left.value=copy->right.value=copy->addressIndex.value=-1;
    copy->use=copy->def=(wide_bits){{0}};
    dbuf_printf(f->diagnostics,"; wide pointer-copy propagated function=%s node=%d source=%d destination=%d uses=%u\n",currFunc->name,i,src,dst,uses);
    ++copies;
  }
  if(copies) {
    wide_reset_allocation(f);
  }
  return copies;
}

/* 单定义对象地址的支配缓存只描述值来源，不描述或缓存对象内容。
 * 使用真实 CFG（包括 switch 全部表项）：移除定义节点后，仍可从入口
 * 到达的节点不受该定义支配。不可达节点不作任何优化推断。 */
typedef struct {
  int entry,definitions[WV_MAX],definition[WV_MAX];
  unsigned char reachable[WN_MAX],ready[WV_MAX],without[WV_MAX][WN_MAX];
} wide_address_dominance;

static void
wide_address_reachable(wide_function *f,int entry,int omitted,unsigned char *seen)
{
  int queue[WN_MAX],head=0,tail=0;
  memset(seen,0,WN_MAX);
  if(entry<0 || entry==omitted)return;
  seen[entry]=1;queue[tail++]=entry;
  while(head<tail) {
    wide_node *n=&f->nodes[queue[head++]];
    for(int k=0;k<2+n->jumpCount;++k) {
      int next=k<2?n->succ[k]:f->jumpSuccessors[n->jumpStart+k-2];
      if(next<0 || next==omitted)continue;
      wassert(next<f->nn);if(seen[next])continue;
      wassert(tail<WN_MAX);seen[next]=1;queue[tail++]=next;
    }
  }
}

static bool
wide_address_dominates(wide_function *f,wide_address_dominance *d,int value,int use)
{
  if(value<0 || d->definitions[value]!=1 || use<0 || !d->reachable[use])return false;
  int definition=d->definition[value];
  if(definition==use || !d->reachable[definition])return false;
  if(!d->ready[value]) {
    wide_address_reachable(f,d->entry,definition,d->without[value]);d->ready[value]=1;
  }
  return !d->without[value][use];
}

static bool
wide_fixed_address_value(wide_value *v)
{
  return v->pointer && !v->codePointer && v->width==4 && !v->floating && !v->sign &&
    !v->symbolSnapshot && IS_PTR(v->sym->type) && getSize(v->sym->type)==3 &&
    !isVolatile(v->sym->type) && !isAtomic(v->sym->type);
}

static bool
wide_fixed_address_delta(wide_operand o,int64_t *delta)
{
  if(o.value>=0 || o.floating || o.codePointer ||
     (o.width!=1 && o.width!=2 && o.width!=4))return false;
  uint32_t value=o.literal;
  if(o.sign && o.width<4 && (value&(UINT32_C(1)<<(8*o.width-1))))
    value|=~((UINT32_C(1)<<(8*o.width))-1);
  *delta=value;
  if(o.sign && (value&UINT32_C(0x80000000)))*delta-=INT64_C(1)<<32;
  return true;
}

static unsigned
wide_fixed_stack_addresses(wide_function *f)
{
  if(!f->no)return 0;
  wide_address_dominance *d=Safe_alloc(sizeof *d);memset(d,0,sizeof *d);d->entry=-1;
  int object[WV_MAX],offset[WV_MAX]={0};unsigned changedUses=0;
  for(int v=0;v<f->nv;++v) {object[v]=-1;d->definition[v]=-1;}
  for(int i=0;i<f->nn;++i) {
    if(f->nodes[i].op==FUNCTION) {if(d->entry>=0) {Safe_free(d);return 0;}d->entry=i;}
    for(int v=0;v<f->nv;++v)if(wb_has(f->nodes[i].def,v)) {
      ++d->definitions[v];d->definition[v]=i;
    }
  }
  if(d->entry<0) {Safe_free(d);return 0;}
  wide_address_reachable(f,d->entry,-1,d->reachable);
  /* 只从本地完整对象生长身份，不推断 phi、动态偏移或循环携带值。
   * 派生值的来源定义必须支配派生点，不能以源码排列顺序代替。 */
  bool changed;
  do {
    changed=false;
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];int v=n->result,source=-1,o=-1;int64_t position=0;
      if(v<0 || !d->reachable[i] || d->definitions[v]!=1 || d->definition[v]!=i ||
         object[v]>=0 || !wide_fixed_address_value(&f->values[v]))continue;
      if(n->op==WM_ADDRESS && n->addressObject>=0 && !n->addressOp) {
        o=n->addressObject;position=n->addressOffset;
        if(o>=f->no || n->addressSymbol!=f->objects[o].sym)continue;
      } else {
        int64_t delta=0;
        if((n->op=='=' || n->op==CAST) && n->width==4 && n->right.width==4)
          source=n->right.value;
        else if((n->op=='+' || n->op=='-') && n->width==4 && n->left.width==4 &&
                wide_fixed_address_delta(n->right,&delta)) {
          source=n->left.value;if(n->op=='-')delta=-delta;
        } else continue;
        if(source<0 || source==v || object[source]<0 ||
           !wide_fixed_address_value(&f->values[source]) ||
           DCL_TYPE(f->values[source].sym->type)!=DCL_TYPE(f->values[v].sym->type) ||
           !wide_address_dominates(f,d,source,i))continue;
        o=object[source];position=offset[source]+delta;
      }
      if(o>=0 && position>=0 && position<=f->objects[o].size) {
        object[v]=o;offset[v]=(int)position;changed=true;
      }
    }
  } while(changed);
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];int source=n->left.value;
    if((n->op!=WM_LOAD && n->op!=WM_STORE) || n->addressObject>=0 || n->codeAccess ||
       source<0 || n->left.width!=4 || object[source]<0 || !wide_address_dominates(f,d,source,i))continue;
    int o=object[source];int64_t position=offset[source],delta=0;
    if(n->addressOp) {
      if((n->addressOp!='+' && n->addressOp!='-') ||
         !wide_fixed_address_delta(n->addressIndex,&delta))continue;
      position+=n->addressOp=='+'?delta:-delta;
    }
    int physical=n->pointerAccess?3:n->width;
    if(physical<1 || position<0 || position>f->objects[o].size ||
       physical>f->objects[o].size-position)continue;
    /* 留在原节点的字节/字/三字节指针事务保持原序、原宽度；不跨越
     * CALL 缓存内容。自存指针仍是右值，不能清除其真正的读取。 */
    n->addressObject=o;n->addressOffset=(int)position;n->addressSymbol=f->objects[o].sym;
    n->left.value=-1;n->addressOp=0;n->addressIndex=(wide_operand){.value=-1};
    if(n->right.value!=source) {wide_bits removed={{0}};wb_set(&removed,source);n->use=wb_minus(n->use,removed);}
    dbuf_printf(f->diagnostics,"; wide stack-address folded function=%s node=%d source=%d definition=%d object=%d offset=%d physical=%d volatile-bytes=%d\n",
      currFunc->name,i,source,d->definition[source],o,(int)position,physical,n->byteAccess);
    ++changedUses;
  }
  Safe_free(d);
  if(changedUses) {wide_remove_dead_addresses(f);wide_reset_allocation(f);}
  return changedUses;
}

static void
wide_liveness (wide_function *f)
{
  bool changed;
  do {
    changed=false;
    for(int i=f->nn;i-->0;) {
      wide_node *n=&f->nodes[i];wide_bits out={{0}},in;
      for(int k=0;k<2;++k) if(n->succ[k]>=0) out=wb_or(out,f->nodes[n->succ[k]].in);
      for(int k=0;k<n->jumpCount;++k)
        out=wb_or(out,f->nodes[f->jumpSuccessors[n->jumpStart+k]].in);
      in=wb_or(n->use,wb_minus(out,n->def));
      changed |= !wb_equal(n->in,in) || !wb_equal(n->out,out);n->in=in;n->out=out;
    }
  } while(changed);
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];
    for(int a=0;a<f->nv;++a) for(int b=0;b<a;++b) {
      bool conflict=(wb_has(n->in,a)&&wb_has(n->in,b)) ||
        (wb_has(n->def,a)&&wb_has(n->out,b)) || (wb_has(n->def,b)&&wb_has(n->out,a));
      if(conflict) {wb_set(&f->values[a].edges,b);wb_set(&f->values[b].edges,a);}
    }
  }
}

static uint32_t wide_mask (int reg,int width) {return mcs251RegisterBytes((unsigned)reg,(unsigned)width);}
static bool
wide_available (wide_function *f,int value,int reg)
{
  wide_value *v=&f->values[value];uint32_t mask=wide_mask(reg,v->width);
  for(int i=0;i<f->nv;++i) if(wb_has(v->edges,i) && f->values[i].reg>=0 && (mask&wide_mask(f->values[i].reg,f->values[i].width))) return false;
  return true;
}

static bool
wide_has_leaf_call (wide_function *f)
{
  for(int i=0;i<f->nn;++i) {
    uint32_t writes;
    if(f->nodes[i].op==CALL && wide_leaf_writes(f->nodes[i].callee,&writes))return true;
  }
  return false;
}

static unsigned
wide_call_register_cost (wide_function *f,int value,int reg)
{
  unsigned cost=0;uint32_t mask=wide_mask(reg,f->values[value].width);
  for(int i=0;i<f->nn;++i) {
    wide_node *n=&f->nodes[i];uint32_t writes;
    if(n->op!=CALL || n->result==value || !wb_has(n->out,value) ||
       !wide_leaf_writes(n->callee,&writes))continue;
    /* 这里只引导寄存器选择，不据此删除保存。参数和结果的真实写集合仍由
     * 发射器重新核验；包括 A/R11、B/R10 及并行实参复制的保留暂存区。 */
    writes|=MCS251_AB_BYTES|wide_mask(16,4)|wide_mask(20,4);
    if(!n->ordinaryCall) {
      if(n->args)writes|=wide_mask(8-n->left.width,n->left.width);
      if(n->args>1)writes|=wide_mask(16-n->right.width,n->right.width);
      if(n->result>=0)writes|=wide_mask(n->width==1?6:8-n->width,n->width);
    }
    if(mask&writes)cost+=1+4*n->origin->depth;
  }
  return cost;
}

static const int *
wide_register_choices(int width,int *count)
{
  static const int dwords[]={4,12,24,28,0};
  static const int words[]={6,14,24,26,28,30,2,0,4,12,8};
  static const int bytes[]={9,8,7,15,3,2,1,0,6,5,4,14,13,12};
  *count=width==4?5:width==2?11:14;
  return width==4?dwords:width==2?words:bytes;
}

static void
wide_layout_spills(wide_function *f)
{
  {
    int objects=0;
    for(int i=0;i<f->no;++i)objects+=f->objects[i].size;
    f->frame=objects;
    /* 对象不可复用；只有未取址的 MIR 溢出值可依冲突图共享字节槽。
     * 按完整宽度检查所有相交区域，支持不同宽度但不依赖同起点。 */
    for(int i=0;i<f->nv;++i)if(f->values[i].reg<0) {
      wide_value *v=&f->values[i];int slot=objects;
      for(;;++slot) {
        bool available=true;
        for(int j=0;j<i;++j) {
          wide_value *other=&f->values[j];
          if(other->reg<0 && wb_has(v->edges,j) && slot<other->slot+other->width && other->slot<slot+v->width) {available=false;break;}
        }
        if(available)break;
      }
      v->slot=slot;if(slot+v->width>f->frame)f->frame=slot+v->width;
    }
  }
}

static bool
wide_validate_allocation(wide_function *f)
{

  /* 分配完成后的自洽性失败是编译器错误，不能假装为语言边界而回退。
   * 在掩码比较之前校验编码范围，避免无效切片的零掩码隐去冲突。 */
  if(WA_FAIL("internal-conflict") && f->nv)f->values[0].reg=33;
  for(int i=0;i<f->nv;++i) if(f->values[i].reg>=0 &&
    (!mcs251RegisterEncodable(f->values[i].reg,f->values[i].width) ||
     !wide_available(f,i,f->values[i].reg))) {f->allocationInvalid=true;return false;}
  for(int i=0;i<f->nv;++i)if(f->values[i].reg<0) {
    wide_value *a=&f->values[i];
    if(a->slot<0) {f->allocationInvalid=true;return false;}
    for(int j=0;j<f->no;++j)if(a->slot<f->objects[j].slot+f->objects[j].size && f->objects[j].slot<a->slot+a->width) {f->allocationInvalid=true;return false;}
    for(int j=0;j<i;++j) {
      wide_value *b=&f->values[j];
      if(b->reg<0 && wb_has(a->edges,j) && a->slot<b->slot+b->width && b->slot<a->slot+a->width) {f->allocationInvalid=true;return false;}
    }
  }
  return f->frame<=512;
}

static bool
wide_allocate (wide_function *f, int strategy, bool callAware)
{
  for(int i=0;i<f->nv;++i) if(f->values[i].pin>=0) {
    if(!wide_available(f,i,f->values[i].pin)) return false;
    f->values[i].reg=f->values[i].pin;
  }
  for(int done=0;done<f->nv;++done) {
    int best=-1;
    unsigned bestScore=0;
    for(int i=0;i<f->nv;++i) if(f->values[i].reg<0 && f->values[i].slot<0) {
      unsigned degree=0,score=f->values[i].weight;
      if(strategy>=2) {
        for(int j=0;j<f->nv;++j)if(wb_has(f->values[i].edges,j))degree+=f->values[j].width;
        /* 长期活值的冲突范围大，不能只凭读取次数占住所有宽寄存器。
         * 两种固定次序用于候选比较，不进行随机或无界搜索。 */
        score=1024*(score+(strategy==3))/(1+degree);
      }
      if(best<0 || score>bestScore) {best=i;bestScore=score;}
    }
    if(best<0) break;
    wide_value *v=&f->values[best];int count;const int *choices=wide_register_choices(v->width,&count);
    if(callAware) {
      unsigned bestCost=0;
      /* 原返回偏好作为同成本时的首选。只在已绑定的叶调用上比较完整
       * 物理切片，不拆宽值、不改冲突图和 ABI 固定参数寄存器。 */
      for(int j=-1;j<count;++j) {
        int reg=j<0?v->preference:choices[j];
        if(reg<0 || !wide_available(f,best,reg))continue;
        unsigned cost=wide_call_register_cost(f,best,reg);
        if(v->reg<0 || cost<bestCost) {v->reg=reg;bestCost=cost;}
      }
    } else {
      if(v->preference>=0 && wide_available(f,best,v->preference)) v->reg=v->preference;
      for(int j=0;j<count && v->reg<0;++j) if(wide_available(f,best,choices[j])) v->reg=choices[j];
    }
    if(v->reg<0) {v->slot=f->frame;f->frame+=v->width;}
  }
  if(strategy)wide_layout_spills(f);
  return wide_validate_allocation(f);
}

#include "wide_emit.c.inc"
#include "wide_cost.h"

/* 只使用已通过 MIR 准入的内存节点，不查猜测性的全局名称表。
 * extern direct 对象也有存储类型；CODE/XDATA、栈对象及越界绝对地址
 * 不得冒充 direct8。候选复制后符号仍为同一编译单元的只读身份。 */
static wide_cost_operand
wide_resolve_cost_operand(const char *text,const void *context)
{
  const wide_function *f=context;
  wide_cost_operand unknown={WC_UNKNOWN,0,0};
  for(int i=0;i<f->nn;++i) {
    const wide_node *n=&f->nodes[i];symbol *s=n->addressSymbol;
    bool bit=n->op==WM_BIT_LOAD || n->op==WM_BIT_STORE || n->op==WM_BIT_CAST;
    if(!s || (!bit && n->op!=WM_SYMBOL_LOAD && n->op!=WM_SYMBOL_STORE))continue;
    memmap *m=SPEC_OCLS(s->etype);
    if(!m || s->onStack || s->_isparm)continue;
    if(bit) {if(!IN_BITSPACE(m))continue;}
    else if(!IN_DIRSPACE(m) || IN_BITSPACE(m) || IN_CODESPACE(m) ||
      IN_FARSPACE(m) || IN_PAGEDSPACE(m) || (IN_REGSP(m) && m!=sfr))continue;
    int offset=wide_cost_symbol_offset(text,s->rname,bit?1:(unsigned)getSize(s->type));
    if(offset<0)continue;
    if(SPEC_ABSA(s->etype) && (SPEC_ADDR(s->etype)<0 || SPEC_ADDR(s->etype)>255-offset))continue;
    return (wide_cost_operand){bit?WC_BIT:WC_DIRECT,0,0};
  }
  return unknown;
}

static wide_byte_cost
wide_emitted_bytes(const wide_function *f,const char *text,size_t length)
{
  return wide_function_bytes_context(text,length,wide_resolve_cost_operand,f);
}

typedef struct { unsigned bytes,unknown,heuristic,instructions,spills;int frame; } wide_selection_cost;

typedef struct {
  char *text;size_t length;unsigned heuristic,instructions,spills;int frame;wide_byte_cost size;
} wide_allocation_candidate;

/* 接收私有分配副本，所有方案使用同一发射器、调用保存和完整编码成本。 */
static void
wide_emit_candidate(wide_function *f,wide_function *trial,wide_allocation_candidate *c)
{
      wide_function *reemit=NULL;
      if(wideLeafEffects) {reemit=Safe_alloc(sizeof *reemit);memcpy(reemit,trial,sizeof *reemit);}
      trial->emitWeight=1;dbuf_init(&trial->output,1024);wide_emit(trial);
      if(reemit) {
        bool useful=false;
        for(int i=0;i<trial->nn;++i) {reemit->nodes[i].callSaveOmit=trial->nodes[i].callSaveOmit;useful|=trial->nodes[i].callSaveOmit!=0;}
        if(useful) {
          /* 从相同分配结果重新发射，所有 SPX 位移依据新的 stackDepth 计算。
           * 第二遍只要实际参数/结果影响触及省略值，就丢弃整个候选。 */
          reemit->applyCallEffects=true;reemit->emitWeight=1;dbuf_init(&reemit->output,1024);wide_emit(reemit);
          wide_byte_cost oldCost=wide_emitted_bytes(trial,dbuf_get_buf(&trial->output),dbuf_get_length(&trial->output));
          wide_byte_cost newCost=wide_emitted_bytes(reemit,dbuf_get_buf(&reemit->output),dbuf_get_length(&reemit->output));
          bool cheaper=!oldCost.unknown && !newCost.unknown && newCost.bytes<=oldCost.bytes && reemit->emitCost<=trial->emitCost;
          if(!reemit->callEffectsInvalid && cheaper) {
            dbuf_destroy(&trial->output);Safe_free(trial);trial=reemit;reemit=NULL;
          } else {
            dbuf_printf(f->diagnostics,"; wide call-save candidate rejected: function=%s effect-conflict=%u cost=%u\n",currFunc->name,reemit->callEffectsInvalid,!cheaper);
            dbuf_destroy(&reemit->output);
          }
        }
        Safe_free(reemit);
      }
      c->length=dbuf_get_length(&trial->output);
      c->text=Safe_alloc(c->length);
      memcpy(c->text,dbuf_get_buf(&trial->output),c->length);
      c->heuristic=trial->emitCost;c->instructions=trial->emitInstructions;c->frame=trial->frame;
      for(int v=0;v<trial->nv;++v)if(trial->values[v].reg<0)++c->spills;
      c->size=wide_emitted_bytes(trial,c->text,c->length);
      dbuf_destroy(&trial->output);
      Safe_free(trial);
}

#include "wide_recolor.c.inc"

static mcs251_wide_result
wide_choose_allocation(wide_function *f,wide_selection_cost *cost)
{
  wide_allocation_candidate c[8]={{0}};
  int selected=-1;bool allKnown=true,invalid=false;unsigned bestCost=0;
  /* 不扩大候选准入范围。size在所有成功候选均可定长时比较最终编码字节；
   * 任一候选含未知形式，则整组保留原指令数策略，不能奖励未知成本。
   * speed仍为循环深度加权启发式，不声称等于芯片周期。 */
  bool callAware=wide_has_leaf_call(f);
  int baseCount=1,count=callAware?2:1;
  for(int strategy=0;strategy<count;++strategy) {
    wide_function *trial=Safe_alloc(sizeof *trial);memcpy(trial,f,sizeof *trial);
    bool allocated=wide_allocate(trial,strategy%baseCount,strategy>=baseCount);
    if(trial->allocationInvalid) {invalid=true;Safe_free(trial);break;}
    if(allocated && WA_FAIL("after-allocate"))allocated=false;
    if(!strategy && f->no) {
      for(int i=0;i<trial->nv;++i)if(trial->values[i].reg<0) {baseCount=4;count=callAware?8:4;break;}
    }
    if(allocated) {
      wide_emit_candidate(f,trial,&c[strategy]);trial=NULL;
      if(strategy<baseCount && c[strategy].size.unknown)allKnown=false;
    }
    Safe_free(trial);
  }
  for(int i=0;!invalid && i<count;++i)if(c[i].text) {
    /* 先按原策略选出基线。新方案必须同时不增加确定编码字节、加权
     * 指令代价及栈帧；未知成本不准入。静态代价不能代替实板周期。 */
    if(i>=baseCount && (selected<0 || c[i].size.unknown || c[selected].size.unknown ||
       c[i].size.bytes>c[selected].size.bytes || c[i].heuristic>c[selected].heuristic ||
       c[i].frame>c[selected].frame))continue;
    unsigned cost=optimize.codeSize?(allKnown?c[i].size.bytes:c[i].instructions):c[i].heuristic;
    if(selected<0 || cost<bestCost || (cost==bestCost && c[i].frame<c[selected].frame)) {selected=i;bestCost=cost;}
  }
  if(selected>=0 && !invalid) {
    wide_function *allocation=Safe_alloc(sizeof *allocation);memcpy(allocation,f,sizeof *allocation);
    if(wide_allocate(allocation,selected%baseCount,selected>=baseCount))
      wide_choose_recolor(f,allocation,&c[selected]);
    if(allocation->allocationInvalid)invalid=true;
    Safe_free(allocation);
  }
  /* 观察现有候选，不改变准入或选择策略。完整发射文本作指纹，方便识别重复方案。 */
  if(selected>=0 && !invalid) {
    int instructionWinner=-1;unsigned instructionBest=0;
    for(int i=0;i<count;++i)if(c[i].text) {
      if(instructionWinner<0 || c[i].instructions<instructionBest ||
         (c[i].instructions==instructionBest && c[i].frame<c[instructionWinner].frame)) {
        instructionWinner=i;instructionBest=c[i].instructions;
      }
    }
    for(int i=0;i<count;++i) {
      dbuf_printf(f->diagnostics,"; wide candidate-cost function=%s id=%d heuristic=%u spills=%u base=%d original=%d\n",
        currFunc->name,i,c[i].heuristic,c[i].spills,baseCount,count);
      uint32_t fingerprint=UINT32_C(2166136261);
      for(size_t j=0;j<c[i].length;++j)fingerprint=(fingerprint^(unsigned char)c[i].text[j])*UINT32_C(16777619);
      dbuf_printf(f->diagnostics,"; wide candidate function=%s id=%d success=%d instructions=%u bytes=%u unknown=%u frame=%d text-fnv=%08lx instruction-winner=%d selected=%d\n",
        currFunc->name,i,c[i].text!=NULL,c[i].instructions,c[i].size.bytes,c[i].size.unknown,c[i].frame,(unsigned long)fingerprint,instructionWinner,selected);
    }
    dbuf_printf(f->diagnostics,"; wide allocation selected=%d candidates=%d heuristic=%u instructions=%u frame=%d\n",selected,count,c[selected].heuristic,c[selected].instructions,c[selected].frame);
    dbuf_printf(f->diagnostics,"; wide cost objective=%s known-bytes=%u unknown=%u group-known=%d\n",optimize.codeSize?(allKnown?"encoded-bytes":"instruction-fallback"):"weighted-instructions",c[selected].size.bytes,c[selected].size.unknown,allKnown);
    if(cost)*cost=(wide_selection_cost){c[selected].size.bytes,c[selected].size.unknown,c[selected].heuristic,c[selected].instructions,c[selected].spills,c[selected].frame};
    dbuf_append(f->candidateOutput,c[selected].text,c[selected].length);
  }
  for(int i=0;i<count;++i)Safe_free(c[i].text);
  return invalid?MCS251_WIDE_ERROR:selected>=0?MCS251_WIDE_ACCEPTED:MCS251_WIDE_FALLBACK;
}

/* 未选择方案的诊断保留为带前缀注释，避免伪装成最终分配记录。 */
static void
wide_pointer_copy_audit(struct dbuf_s *output,const char *kind,struct dbuf_s *input)
{
  const char *text=dbuf_get_buf(input);size_t length=dbuf_get_length(input);
  for(size_t at=0;at<length;) {
    size_t end=at;while(end<length && text[end]!='\n')++end;
    dbuf_printf(output,"; wide pointer-copy %s ",kind);dbuf_append(output,text+at,end-at);
    dbuf_append_str(output,"\n");at=end+1;
  }
}

/* 各 MIR 变换共享完整函数的候选比较；不叠加局部收益猜测。
 * 内部错误一直向上传播，未知编码或任一成本退步时保留完整旧候选。 */
static mcs251_wide_result
wide_choose_transform(wide_function *f,wide_selection_cost *cost,
  unsigned (*transform)(wide_function *),
  mcs251_wide_result (*choose)(wide_function *,wide_selection_cost *),
  const char *kind,const char *countName,bool requireSpill,
  const char *originalKind,const char *trialKind)
{
  wide_function *trial=Safe_alloc(sizeof *trial);memcpy(trial,f,sizeof *trial);
  struct dbuf_s originalCode,originalDiagnostics,trialCode,trialDiagnostics;
  dbuf_init(&originalCode,1024);dbuf_init(&originalDiagnostics,256);
  dbuf_init(&trialCode,1024);dbuf_init(&trialDiagnostics,256);
  trial->candidateOutput=&trialCode;trial->diagnostics=&trialDiagnostics;
  unsigned changes=transform(trial);
  mcs251_wide_result result;
  if(!changes)result=choose(f,cost);
  else {
    struct dbuf_s *code=f->candidateOutput,*diagnostics=f->diagnostics;
    wide_selection_cost oldCost={0},newCost={0};bool selected=false;
    f->candidateOutput=&originalCode;f->diagnostics=&originalDiagnostics;
    result=choose(f,&oldCost);
    f->candidateOutput=code;f->diagnostics=diagnostics;
    mcs251_wide_result originalResult=result,trialResult=MCS251_WIDE_FALLBACK;
    if(result==MCS251_WIDE_ACCEPTED && (!requireSpill || oldCost.spills)) {
      wide_liveness(trial);trialResult=choose(trial,&newCost);
      if(trialResult==MCS251_WIDE_ERROR)result=MCS251_WIDE_ERROR;
      else selected=trialResult==MCS251_WIDE_ACCEPTED && !oldCost.unknown && !newCost.unknown &&
        newCost.bytes<=oldCost.bytes && newCost.heuristic<=oldCost.heuristic && newCost.frame<=oldCost.frame &&
        (newCost.bytes<oldCost.bytes || newCost.heuristic<oldCost.heuristic || newCost.frame<oldCost.frame);
    }
    dbuf_printf(diagnostics,"; wide %s choice function=%s %s=%u selected=%u original-result=%d trial-result=%d old-bytes=%u new-bytes=%u old-unknown=%u new-unknown=%u old-weighted=%u new-weighted=%u old-frame=%d new-frame=%d old-spills=%u new-spills=%u\n",
      kind,currFunc->name,countName,changes,selected,originalResult,trialResult,oldCost.bytes,newCost.bytes,oldCost.unknown,newCost.unknown,
      oldCost.heuristic,newCost.heuristic,oldCost.frame,newCost.frame,oldCost.spills,newCost.spills);
    struct dbuf_s *chosenCode=selected?&trialCode:&originalCode;
    struct dbuf_s *chosenDiagnostics=selected?&trialDiagnostics:&originalDiagnostics;
    wide_pointer_copy_audit(diagnostics,selected?originalKind:trialKind,selected?&originalDiagnostics:&trialDiagnostics);
    dbuf_append(diagnostics,dbuf_get_buf(chosenDiagnostics),dbuf_get_length(chosenDiagnostics));
    if(result==MCS251_WIDE_ACCEPTED)dbuf_append(code,dbuf_get_buf(chosenCode),dbuf_get_length(chosenCode));
    if(cost && result==MCS251_WIDE_ACCEPTED)*cost=selected?newCost:oldCost;
  }
  dbuf_destroy(&originalCode);dbuf_destroy(&originalDiagnostics);
  dbuf_destroy(&trialCode);dbuf_destroy(&trialDiagnostics);Safe_free(trial);
  return result;
}

static mcs251_wide_result
wide_choose_pointer_copy(wide_function *f,wide_selection_cost *cost)
{
  return wide_choose_transform(f,cost,wide_entry_pointer_copies,wide_choose_allocation,
    "pointer-copy","copies",true,"original","trial");
}

/* 旧选择器的顺序和门槛不变：固定栈地址包住指针副本，后者仍只在
 * 旧分配候选存在 spill 时准入；向外提供完整候选成本供调度比较。 */
static mcs251_wide_result
wide_choose_stack_addresses(wide_function *f,wide_selection_cost *cost)
{
  return wide_choose_transform(f,cost,wide_fixed_stack_addresses,wide_choose_pointer_copy,
    "stack-address","accesses",false,"stack-original","stack-trial");
}

/* 新调度只与完整旧选择器的输出比较，不能提前消掉 spill 而使原有
 * 指针副本候选失去准入资格，再错误地把局部胜出当作整体不退步。 */
#include "wide_local_copies.c.inc"

static mcs251_wide_result
wide_choose_local_pointer_copy(wide_function *f,wide_selection_cost *cost)
{
  return wide_choose_transform(f,cost,wide_local_pointer_copies,wide_choose_stack_addresses,
    "local-pointer-copy","copies",false,"local-original","local-trial");
}

static mcs251_wide_result
wide_choose_integer_sinking(wide_function *f,wide_selection_cost *cost)
{
  return wide_choose_transform(f,cost,wide_sink_integer_expressions,wide_choose_local_pointer_copy,
    "integer-sink","moves",true,"sink-original","sink-trial");
}

static bool
wide_is_function_body (ebbIndex *ebbi)
{
  unsigned entries = 0, ends = 0;
  /* 静态局部对象的初始化也经过寄存器分配，此时 currFunc 仍指向
   * 所属函数，但片段不含函数边界。只让完整函数进入宽发射器，
   * 防止在 GSINIT 中重复输出函数标签及函数入口/退出代码。 */
  for (int b = 0; b < ebbi->count; ++b)
    {
      eBBlock *bb = ebbi->bbOrder[b];
      for (iCode *ic = bb->sch; ic; ic = ic == bb->ech ? NULL : ic->next)
        if (ic->op == FUNCTION || ic->op == ENDFUNCTION)
          {
            if (!IS_SYMOP (IC_LEFT (ic)) || OP_SYMBOL (IC_LEFT (ic)) != currFunc)
              return false;
            if (ic->op == FUNCTION) ++entries;
            else ++ends;
          }
    }
  return entries == 1 && ends == 1;
}

static mcs251_wide_result
wide_try_function (ebbIndex *ebbi, struct dbuf_s *output, struct dbuf_s *diagnostics)
{
  mcs251_wide_result result=MCS251_WIDE_FALLBACK;wide_function *f;
  if(!currFunc)return MCS251_WIDE_FALLBACK;
  if(!mcs251WideEnabled) {dbuf_printf(diagnostics,"; wide admission function=%s skip=disabled\n",currFunc->name);return MCS251_WIDE_FALLBACK;}
  if(!wide_is_function_body(ebbi)) {dbuf_printf(diagnostics,"; wide admission function=%s skip=non-function-initializer\n",currFunc->name);return MCS251_WIDE_FALLBACK;}
  bool ordinary=FUNC_SDCCCALL(currFunc->type)==2;
  if((FUNC_SDCCCALL(currFunc->type)!=4 && !ordinary) ||
     (ordinary && (!mcs251WideOrdinaryEnabled || currFunc->cdef ||
                   !(IFFUNC_ISREENT(currFunc->type) || options.stackAuto))) ||
     options.debug || options.useXstack || options.noRegParams ||
     IFFUNC_ISISR(currFunc->type) || IFFUNC_ISNAKED(currFunc->type) ||
     IFFUNC_ISCRITICAL(currFunc->type) || IFFUNC_CALLEESAVES(currFunc->type) ||
     IFFUNC_ISBANKEDCALL(currFunc->type) || FUNC_REGBANK(currFunc->type) ||
     IFFUNC_HASVARARGS(currFunc->type) ||
     (!wide_type(currFunc->type->next) && !IS_VOID(currFunc->type->next))) {dbuf_printf(diagnostics,"; wide admission function=%s skip=entry-contract\n",currFunc->name);return MCS251_WIDE_FALLBACK;}
  /* 宽发射成功会绕过旧 genFunction 的 ABI 校验；CODE 形参须在入口
   * 显式回退，让原校验给出诊断，不能因局部值支持而扩大公开 ABI。 */
  for(value *arg=FUNC_ARGS(currFunc->type);arg;arg=arg->next)
    if(wide_code_pointer(arg->type) || (ordinary && !wide_type(arg->type))) {dbuf_printf(diagnostics,"; wide admission function=%s skip=parameter-type\n",currFunc->name);return MCS251_WIDE_FALLBACK;}
  if(!wide_stack_signature(currFunc->type)) {dbuf_printf(diagnostics,"; wide admission function=%s skip=stack-signature\n",currFunc->name);return MCS251_WIDE_FALLBACK;}
  f=Safe_alloc(sizeof *f);memset(f,0,sizeof *f);f->ordinaryEntry=ordinary;f->diagnostics=diagnostics;f->candidateOutput=output;
  if(wide_import(f,ebbi)) {
    if(WA_FAIL("after-import"))goto fallback;
    bool importedStackObjects=f->no!=0;
    wide_immutable_parameter_homes(f);
    wide_strength_reduce(f);
    wide_stack_addresses(f);
    /* 早期浮点联合体调用因间接栈寻址而延后迁移。现在只在显式普通
     * ABI2 试验开关内，允许所有浮点内存访问已证明落在本地对象范围
     * 的函数使用直接栈访问；不缓存内容、不删 volatile 字节事务。
     * 外部/不明指针及既有 ABI4 路径仍保留原来的体积回退。 */
    bool floatMemory=false,call=false,directFloatMemory=true;
    for(int i=0;i<f->nn;++i) {
      wide_node *n=&f->nodes[i];call|=n->op==CALL;
      bool floating=(n->op==WM_LOAD && n->floating) || (n->op==WM_STORE && n->right.floating);
      floatMemory|=floating;
      if(floating && n->addressObject<0)directFloatMemory=false;
    }
    if(f->sampledQualified && importedStackObjects && floatMemory && call) {
      if(!f->ordinaryEntry || !directFloatMemory) {
        dbuf_printf(diagnostics,"; wide migration deferred: stack float memory with call\n");
        goto fallback;
      }
      dbuf_printf(diagnostics,"; wide stack float call migration: all float accesses direct\n");
    }
    wide_stack_indexed_bases(f);
    wide_fold_indexed_addresses(f);
    wide_reuse_parameter_loads(f);
    wide_fold_extensions(f);
    wide_liveness(f);
    if(WA_FAIL("after-transform"))goto fallback;
    result=wide_choose_integer_sinking(f,NULL);
    if(result==MCS251_WIDE_ACCEPTED && WA_FAIL("after-emit")) {
      dbuf_append_str(output,"\n_mx251_discarded_test_marker:\n\t.db 0xa5\n");
      result=MCS251_WIDE_FALLBACK;
    }
  }
fallback:
  if(result==MCS251_WIDE_FALLBACK) {
    dbuf_printf(diagnostics,"; mcs251 wide-mir fallback: %s\n",currFunc->name);
    /* 保留拒绝位置，便于区分语义边界和容量限制；不改变回退机器码。 */
    if(f->nn)dbuf_printf(diagnostics,"; wide rejection: node=%d icode=%d opcode=%u values=%d objects=%d frame=%d\n",
                        f->nn,f->nodes[f->nn-1].origin->key,f->nodes[f->nn-1].origin->op,f->nv,f->no,f->frame);
  }
  Safe_free(f);return result;
}


mcs251_wide_result
mcs251TryWideFunction (ebbIndex *ebbi)
{
  struct dbuf_s output, diagnostics;
  mcs251_wide_result result;
  dbuf_init(&output,1024);dbuf_init(&diagnostics,256);
#ifdef MX251_WIDE_AUDIT
  wa_snapshot *before=wa_begin(ebbi);
#endif
  result=wide_try_function(ebbi,&output,&diagnostics);
#ifdef MX251_WIDE_AUDIT
  if(WA_FAIL("mutate-input") && ebbi->count && ebbi->bbOrder[0]->sch)
    ebbi->bbOrder[0]->sch->key^=1;
  if(!wa_end(before,result))result=MCS251_WIDE_ERROR;
#endif
  /* 诊断不是候选代码：拒绝时保留原因，丢弃全部标签、指令和栈操作。
   * 内部错误明确失败，旧后端不能把不一致状态重新解释成可用代码。 */
  if(dbuf_get_length(&diagnostics))dbuf_append(codeOutBuf,dbuf_get_buf(&diagnostics),dbuf_get_length(&diagnostics));
  if(result==MCS251_WIDE_ACCEPTED && dbuf_get_length(&output))
  {
    /* 只发布最终接受的候选；回退/失败和分配试算不能污染函数摘要。
     * 本版仅审计，不以 may-write 上界改变 ABI 或调用保存。 */
    mx251_effects e=mx251TextEffects(dbuf_get_buf(&output),dbuf_get_length(&output));
    dbuf_printf(&output,"; mx251-effects schema=1 function=%s abi=%u complete=%u reads=%08lx writes=%08lx specialReads=%08lx specialWrites=%08lx flagsReads=%08lx flagsWrites=%08lx memory=%u instructions=%u unknown=%u calls=%u\n",
      currFunc->rname,(unsigned)FUNC_SDCCCALL(currFunc->type),!e.unknown,
      (unsigned long)e.reads,(unsigned long)e.writes,(unsigned long)e.specialReads,(unsigned long)e.specialWrites,
      (unsigned long)e.flagsReads,(unsigned long)e.flagsWrites,e.memory,e.instructions,e.unknown,e.calls);
    dbuf_append(codeOutBuf,dbuf_get_buf(&output),dbuf_get_length(&output));
  }
  dbuf_destroy(&output);dbuf_destroy(&diagnostics);
  if(result==MCS251_WIDE_ERROR)
    werror(E_INTERNAL_ERROR,__FILE__,__LINE__,"mcs251 wide invariant failed; legacy fallback prohibited");
  return result;
}
