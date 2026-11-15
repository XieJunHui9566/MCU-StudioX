/* SPDX-License-Identifier: GPL-2.0-or-later
 * 每条已知指令统一按六字节上界计算，不试图复刻汇编器精确编码。
 * 当前源/二进制映射的指令矩阵最大五字节；保留一字节余量。
 * 未知指令/指令伪操作成为屏障，容量溢出或不完整行则整函数不缩短。
 * 仅记录明确的 MIR 条件/GOTO；跳转表项和尾调用从不进入候选。
 */
static unsigned
wbranch_instruction_bound(const char *text,size_t length)
{
  static const char *known[]={"add","sub","cmp","inc","dec","mul","anl","orl","xrl",
    "mov","movs","movz","movc","push","pop","eret","sra","srl","rl","rr","swap","clr",
    "ejmp","ecall","jc","jnc","je","jne","jg","jle","jsl","jsge","jsg","jsle","sjmp"};
  size_t n=0;while(n<length && text[n]>='a' && text[n]<='z')++n;
  if(n<length && text[n]!='\t' && text[n]!=' ')return 0;
  for(unsigned i=0;i<sizeof known/sizeof known[0];++i)if(strlen(known[i])==n && !strncmp(text,known[i],n))return 6;
  return 0;
}
static void
wrelax_branches(wide_function *f)
{
  if(f->relaxDisabled || !f->branchCount)return;
  struct dbuf_s out;dbuf_init(&out,dbuf_get_length(&f->output));
  const char *source=dbuf_get_buf(&f->output);size_t copied=0;unsigned shortened=0;
  for(unsigned i=0;i<f->branchCount;++i) {
    wide_branch_site *b=&f->branches[i];wide_label_site *target=NULL;bool duplicate=false;
    for(unsigned j=0;j<f->labelCount;++j)if(f->labels[j].label==b->target) {if(target)duplicate=true;target=&f->labels[j];}
    if(!target || duplicate || target->barrier!=b->barrier)continue;
    unsigned limit;bool forward=target->text>=b->end;
    if(forward) {limit=target->upper-b->upperEnd;if(limit>127)continue;}
    else if(target->text<=b->begin) {
      /* 短条件在binary映射最多三字节；SJMP两字节。向后还需计入自身。 */
      limit=b->upperBegin-target->upper+(b->condition?3:2);if(limit>128)continue;
    } else continue;
    /* 所有决策都依据原始布局上界，后续只缩短，没有对齐/插入。
     * 前向中间区间与后向区间因此只能变短，不需要迭代或冒险放宽阈值。 */
    wassert(b->begin>=copied && b->end>=b->begin);
    dbuf_append(&out,source+copied,b->begin-copied);
    dbuf_printf(&out,"; wide short branch direction=%s bound=%u kind=%s\n\t%s\t__wm_%s_%d\n",
                forward?"forward":"backward",limit,b->condition?"conditional":"goto",
                b->condition?b->condition:"sjmp",currFunc->rname,b->target);
    copied=b->end;++shortened;
    if(b->condition) {--f->emitInstructions;f->emitCost-=b->weight;}
  }
  if(shortened) {
    dbuf_append(&out,source+copied,dbuf_get_length(&f->output)-copied);
    dbuf_destroy(&f->output);f->output=out;
  } else dbuf_destroy(&out);
}
