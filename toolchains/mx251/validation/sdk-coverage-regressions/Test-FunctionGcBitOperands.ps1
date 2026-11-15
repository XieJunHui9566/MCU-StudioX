#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. $Engine
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output already exists'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Save([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function Fn([string]$Name,[string]$Body){return "; mx251-function begin schema=1 symbol=$Name isr=0 addressTaken=0 opaque=0 indirect=0`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"}
function Case([string]$Name,[string]$Body,[bool]$Fallback=$false,[string]$After='',[string]$DeadLabel='',[string[]]$ExpectedRemoved=@('_dead')){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $asm=Join-Path $dir 'unit.asm';$rel=Join-Path $dir 'unit.rel'
 Save $asm (".module test`n.globl _main,_dead`n.area CSEG (CODE)`n"+(Fn '_main' ($Body+"`neret`n00100`$:`neret"))+"`n"+(Fn '_dead' ($DeadLabel+"`neret"))+"`n"+$After)
 Save $rel "XH3`nH 1 areas 0 global symbols`nM test`nA CSEG size 0 flags 20 addr 0"
 $result=Invoke-Mx251FunctionGc -Root $dir -Units @(@{name='unit';assembly=$asm;object=$rel;language='c'}) -KeepSymbols @('_main')
 $manifest=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable
 $expected=if($Fallback){@()}else{@($ExpectedRemoved)}
 if($result.Fallback -ne $Fallback -or (@($result.RemovedFunctions|Sort-Object)-join',') -cne (@($expected|Sort-Object)-join',')){throw "FAILED $Name $(ConvertTo-Json -InputObject $manifest -Depth 20 -Compress)"}
 if($Fallback){foreach($unit in $manifest.units){if($unit.inputSha256 -cne $unit.candidateSha256){throw "Non-exact fallback: $Name"}}}
 $results.Add(@{name=$Name;passed=$true;fallback=$result.Fallback;removed=@($result.RemovedFunctions);manifest=$result.Manifest})
}
for($bit=0;$bit -le 7;$bit++){
 $alias="acc.$bit"
 Case "acc-bit-$bit" "jb $alias,00100`$`njnb $alias,00100`$`njbc $alias,00100`$`nmov c,$alias`nmov $alias,cy`nanl c,/$alias`norl cy,$alias`nsetb $alias`nclr $alias`ncpl $alias"
 Case "numeric-bit-$bit" "jb 0x30.$bit,00100`$`nmov s:0x92.$bit,cy`nanl c,/s:0x92.$bit`nsetb 255.$bit`nclr 0x20.$bit"
}
$other=@();foreach($base in @('a','b','p0','p1','p2','p3','psw','scon','t2con','tcon')){foreach($bit in 0..7){$other+="mov c,$base.$bit"}}
foreach($bit in @(0,1,2,3,4,5,7)){$other+="mov c,ie.$bit"};foreach($bit in 0..5){$other+="mov c,ip.$bit"}
Case 'all-other-predefined-bits' ($other -join "`n")
Case 'case-insensitive-builtins' "mov CY,ACC.7`nanl C,/B.0`nsetb S:0x92.6"
Case 'classic-numeric-bits' "mov c,0x0f`nsetb 255`nclr 0"
Case 'unknown-named-bit' 'mov c,not_a_register.1' $true
Case 'known-register-invalid-bit' 'jb acc.8,00100$' $true
Case 'unknown-sfr-bit' 'mov c,psw1.0' $true
Case 'nonpredefined-ie-bit-conservative' 'mov c,ie.6' $true
Case 'unknown-s-prefixed-symbol' 'mov c,s:unknown.0' $true
Case 'call-must-not-use-bit-whitelist' 'ecall acc.1' $true
Case 'immediate-must-not-use-bit-whitelist' 'mov r0,#acc.1' $true
Case 'data-must-not-use-bit-whitelist' 'nop' $true ".area CONST (CODE)`n.dw acc.1"
Case 'numeric-bit-out-of-range' 'mov c,0x30.8' $true
Case 'numeric-base-out-of-range' 'mov c,0x100.1' $true
Case 'sfr-base-too-low' 'mov c,s:0x30.1' $true
Case 'inverted-bit-disallowed' 'mov c,/acc.1' $true
Case 'secondary-label-preserved' 'mov c,_handler.1' $false '' '_handler.1:' @()
Case 'user-definition-takes-precedence' 'mov c,acc.1' $false '' 'acc.1:' @()
@{status='PASS_BIT_OPERAND_CLOSURE_FIXTURES_ONLY';engineSha256=(Get-Mx251GcHash $Engine);cases=@($results.ToArray());hardwareAccess=$false;compilerInvoked=$false;scope='Exact predefined bit aliases only in bit operand positions; numeric address/bit ranges checked; unknown names and unsafe positions keep all. This is parser and graph qualification, not instruction execution.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) bit-operand fixtures; $OutputDirectory"
