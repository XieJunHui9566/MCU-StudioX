#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. $Engine
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output already exists'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Save([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function Fn([string]$Name,[string]$Body){return "; mx251-function begin schema=1 symbol=$Name isr=0 addressTaken=0 opaque=0 indirect=0`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"}
function Case([string]$Name,[string[]]$Texts,[string]$Extra,[string[]]$Keep,[string[]]$Removed){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir);$units=@();$i=0
 foreach($text in $Texts){$a=Join-Path $dir "u$i.asm";$o=Join-Path $dir "u$i.rel";Save $a $text;Save $o "XH3`nH 1 areas 0 global symbols`nM test`nA CSEG size 0 flags 20 addr 0";$units+=@{name="u$i";assembly=$a;object=$o;language='c'};$i++}
 $extras=@();if($Extra){$a=Join-Path $dir 'startup.asm';Save $a $Extra;$extras=@($a)}
 $result=Invoke-Mx251FunctionGc -Root $dir -Units $units -ExtraAssembly $extras -KeepSymbols $Keep
 $m=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable
 $expected=@($Removed|Sort-Object -CaseSensitive);$actual=@($result.RemovedFunctions|Sort-Object -CaseSensitive)
 if($result.Fallback -or ($expected -join ',') -cne ($actual -join ',')){throw "Decision mismatch $Name"}
 foreach($unit in $m.units){$out=Get-Content -LiteralPath $unit.candidate -Raw;foreach($removed in $Removed){if($out -cmatch ('(?m)^\s*\.globl[^\r\n]*\b'+[regex]::Escape($removed)+'\b')){throw "Orphan removed declaration $Name $removed"}}}
 $results.Add(@{name=$Name;passed=$true;removed=$actual;manifest=$result.Manifest})
}
$prefix=".module test`n.globl _main,_foo,_Foo`n.area CSEG (CODE)"
$main=Fn '_main' 'eret';$lower=Fn '_foo' 'eret';$upper=Fn '_Foo' 'eret'
Case 'startup-lowercase-not-dead-Start' @(".module test`n.globl _main,_Start`n.area CSEG (CODE)`n$main`n$(Fn '_Start' 'eret')") ".area CSEG (CODE)`n.globl _start,_main`n_start:`necall _main" @('_main') @('_Start')
Case 'same-tu-lower-call-only' @("$prefix`n$(Fn '_main' "ecall _foo`neret")`n$lower`n$upper") '' @('_main') @('_Foo')
Case 'same-tu-upper-call-only' @("$prefix`n$(Fn '_main' "ecall _Foo`neret")`n$lower`n$upper") '' @('_main') @('_foo')
Case 'cross-tu-case-distinct-call' @("$prefix`n$(Fn '_main' "ecall _Foo`neret")", ".module second`n.globl _Foo,_foo`n.area CSEG (CODE)`n$lower`n$upper") '' @('_main') @('_foo')
Case 'constant-table-upper-root' @("$prefix`n$main`n$lower`n$upper`n.area CONST (CODE)`n.dw _Foo") '' @('_main') @('_foo')
Case 'explicit-lower-root' @("$prefix`n$main`n$lower`n$upper") '' @('_main','_foo') @('_Foo')
Case 'cross-tu-table-and-call-both' @("$prefix`n$(Fn '_main' "ecall _foo`neret")`n.area CONST (CODE)`n.dw _Foo", ".module second`n.globl _foo,_Foo`n.area CSEG (CODE)`n$lower`n$upper") '' @('_main') @()
Case 'same-local-label-different-tu' @("$prefix`n$(Fn '_main' "ecall _foo`neret")`n$(Fn '_foo' "_local:`neret")", ".module second`n.globl _Foo`n.area CSEG (CODE)`n$(Fn '_Foo' "_local:`neret")") '' @('_main') @('_Foo')
@{status='PASS_CASE_SENSITIVE_SYMBOL_CLOSURE_ONLY';engineSha256=(Get-Mx251GcHash $Engine);cases=@($results.ToArray());hardwareAccess=$false;compilerInvoked=$false}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) symbol-case fixtures; $OutputDirectory"
