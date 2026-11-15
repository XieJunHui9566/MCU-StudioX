#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. $Engine
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output already exists'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Save([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function Fn([string]$Name,[string]$Body){return "; mx251-function begin schema=1 symbol=$Name isr=0 addressTaken=0 opaque=0 indirect=0`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"}
function Case([string]$Name,[string[]]$Imports,[string]$DeadBody,[string[]]$Drop){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $a=Join-Path $dir 'u.asm';$o=Join-Path $dir 'u.rel'
 Save $a (".module test`n.globl _main,_dead,"+($Imports -join ',')+"`n.area CSEG (CODE)`n"+(Fn '_main' 'eret')+"`n"+(Fn '_dead' $DeadBody))
 Save $o ("XH3`nH 1 areas $($Imports.Count.ToString('X')) global symbols`nM test`n"+(($Imports|ForEach-Object{"S $_ Ref000000"}) -join "`n")+"`nA CSEG size 0 flags 20 addr 0")
 $result=Invoke-Mx251FunctionGc -Root $dir -Units @(@{name='u';assembly=$a;object=$o;language='c'}) -KeepSymbols @('_main')
 if($result.Fallback){throw "Unexpected fallback $Name"}
 $m=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable;$u=$m.units[0]
 if((@($u.removedUnusedExternalDeclarations|Sort-Object) -join ',') -cne (@($Drop|Sort-Object) -join ',')){throw "Declaration decision mismatch $Name"}
 $text=Get-Content -LiteralPath $u.candidate -Raw
 foreach($import in $Imports){$present=$text -cmatch ('(?m)^\s*\.globl[^\r\n]*\b'+[regex]::Escape($import)+'\b');if($present -eq ($import -cin $Drop)){throw "Declaration text mismatch $Name $import"}}
 $results.Add(@{name=$Name;passed=$true;removed=$u.removedUnusedExternalDeclarations;manifest=$result.Manifest})
}
Case 'generic-linker-force-declaration' @('_force_archive_member') 'eret' @()
Case 'compiler-startup-force-declarations' @('__mcs51_genRAMCLEAR','__mcs51_genXINIT','__mcs51_genXRAMCLEAR') 'eret' @()
Case 'mixed-dead-use-and-forced-declaration' @('_dead_import','_force_archive_member') "ecall _dead_import`neret" @()
Case 'declaration-in-removed-body-is-not-explicit-use' @('_force_archive_member') ".globl _force_archive_member`neret" @()
Case 'dead-data-use-cannot-prove-external-import-dead' @('_dead_import','_force_archive_member') ".dw _dead_import`neret" @()
@{status='PASS_IMPLICIT_DECLARATION_ROOTS_ONLY';engineSha256=(Get-Mx251GcHash $Engine);cases=@($results.ToArray());hardwareAccess=$false;compilerInvoked=$false;scope='Synthetic declaration retention only; real initialized storage and archive GSINIT checks are separate.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) implicit-root fixtures; $OutputDirectory"
