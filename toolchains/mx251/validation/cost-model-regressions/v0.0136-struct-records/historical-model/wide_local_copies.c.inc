/* SPDX-License-Identifier: GPL-2.0-or-later
 * 同一基本块中的指针值副本传播。只改变地址值的名字，不缓存或移动内存访问。 */
static unsigned
wide_local_pointer_copies(wide_function *f)
{
  unsigned changed=0;
  for(int i=0;i<f->nn;++i) {
    wide_node *copy=&f->nodes[i];
    if((copy->op!='=' && copy->op!=CAST) || copy->result<0 || copy->right.value<0)continue;
    int dst=copy->result,src=copy->right.value;
    if(dst==src || !wide_fixed_address_value(&f->values[src]) ||
       !wide_fixed_address_value(&f->values[dst]) || f->values[dst].pin>=0 ||
       copy->width!=4 || copy->right.width!=4 || copy->right.sign || copy->right.floating || copy->right.codePointer ||
       DCL_TYPE(f->values[src].sym->type)!=DCL_TYPE(f->values[dst].sym->type))continue;
    int definitions=0,last=-1;unsigned uses=0;bool valid=true;
    for(int j=0;j<f->nn;++j) {
      wide_node *n=&f->nodes[j];
      if(wb_has(n->def,dst))++definitions;
      if(!wb_has(n->use,dst))continue;
      bool represented=n->left.value==dst || n->right.value==dst || n->addressIndex.value==dst;
      for(unsigned k=0;k<n->stackArgs;++k)represented|=n->stackOperands[k].value==dst;
      if(j<=i || !represented)valid=false;
      last=j;++uses;
    }
    if(!valid || definitions!=1 || !uses)continue;
    /* 所有使用必须在同一直线区域。源值不能被重定义；调用作为边界，
     * 免得延长调用保存活区间。不同入口、回边和 switch 表项都必须排除。 */
    for(int j=i;j<=last && valid;++j) {
      wide_node *n=&f->nodes[j];
      if(n->op==LABEL || n->op==FUNCTION || n->op==ENDFUNCTION || n->op==CALL || n->op==PCALL ||
         n->jumpCount || n->succ[1]>=0 || (j<last && n->succ[0]!=j+1) ||
         (j>i && wb_has(n->def,src)))valid=false;
    }
    for(int j=0;j<f->nn && valid;++j)for(int k=0;k<2;++k) {
      int to=f->nodes[j].succ[k];
      if(to>i && to<=last && !(k==0 && j+1==to))valid=false;
    }
    for(int j=0;j<f->nj;++j)if(f->jumpSuccessors[j]>i && f->jumpSuccessors[j]<=last)valid=false;
    if(!valid)continue;
    wide_bits destination={{0}};wb_set(&destination,dst);
    for(int j=i+1;j<=last;++j) {
      wide_node *n=&f->nodes[j];if(!wb_has(n->use,dst))continue;
      if(n->left.value==dst)n->left.value=src;
      if(n->right.value==dst)n->right.value=src;
      if(n->addressIndex.value==dst)n->addressIndex.value=src;
      for(unsigned k=0;k<n->stackArgs;++k)if(n->stackOperands[k].value==dst)n->stackOperands[k].value=src;
      n->use=wb_minus(n->use,destination);wb_set(&n->use,src);
    }
    copy->op=WM_NOP;copy->result=copy->left.value=copy->right.value=copy->addressIndex.value=-1;
    copy->use=copy->def=(wide_bits){{0}};
    dbuf_printf(f->diagnostics,"; wide local-pointer-copy node=%d source=%d destination=%d uses=%u last=%d\n",i,src,dst,uses,last);
    ++changed;
  }
  if(changed)wide_reset_allocation(f);
  return changed;
}
