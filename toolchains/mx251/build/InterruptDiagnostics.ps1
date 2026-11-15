# 只翻译链接器已经报告的错误；此处不扫描源代码，也不代替链接器判定成功。
# 原始 stdout/stderr 必须继续保留，未认识的诊断沿用原文。
function Get-Mx251InterruptDiagnostics([string]$Text) {
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($match in [regex]::Matches($Text,'Undefined Global __mx251_irq_(reg|def)_v1_([0-9]+)_([A-Za-z_][A-Za-z0-9_]*)')) {
  if(-not $seen.Add($match.Value)){continue}
  $kind=$match.Groups[1].Value;$slot=[int]$match.Groups[2].Value;$symbol=$match.Groups[3].Value
  if($kind -eq 'reg'){
   [pscustomobject]@{code='MX251-IRQ001';slot=$slot;symbol=$symbol;message="中断未登记：$symbol（槽 $slot）缺少主翻译单元可见的同名同槽声明。请把 __interrupt($slot) 原型放入共享头文件，并由 main 所在文件包含。"}
  }else{
   [pscustomobject]@{code='MX251-IRQ002';slot=$slot;symbol=$symbol;message="中断定义不匹配：槽 $slot 的 $symbol 没有同名同槽的 ISR 定义。请核对 __interrupt($slot)，并用配套 MX251 重新编译对应对象或库成员。"}
  }
 }
 foreach($match in [regex]::Matches($Text,'Multiple definition of __mx251_irq_slot_v1_([0-9]+)')) {
  if(-not $seen.Add($match.Value)){continue}
  $slot=[int]$match.Groups[1].Value
  [pscustomobject]@{code='MX251-IRQ003';slot=$slot;symbol=$null;message="中断槽冲突：槽 $slot 被多个已链接的 ISR 定义占用。每个槽只能保留一个处理函数。"}
 }
}
