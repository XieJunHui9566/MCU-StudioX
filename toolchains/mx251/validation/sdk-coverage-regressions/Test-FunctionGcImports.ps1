#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. $Engine
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output already exists'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Save([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function Fn([string]$Name,[string]$Body,[string]$Opaque='0'){return "; mx251-function begin schema=1 symbol=$Name isr=0 addressTaken=0 opaque=$Opaque indirect=0`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"}
function Rel(){return "XH3`nH 1 areas 1 global symbols`nM test`nS _Start Ref000000`nA CSEG size 0 flags 20 addr 0"}
function Case([string]$Name,[string]$Main='eret',[string]$After='',[string]$Extra='',[bool]$Library=$false,[bool]$ExpectDrop=$false,[bool]$Fallback=$false,[string]$Second=''){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $asm=Join-Path $dir 'unit.asm';$rel=Join-Path $dir 'unit.rel'
 Save $asm (".module test`n.globl _main,_dead,_Start`n.area CSEG (CODE)`n"+(Fn '_main' $Main $(if($Fallback){'1'}else{'0'}))+"`n"+(Fn '_dead' "ecall _Start`neret")+"`n"+$After)
 Save $rel (Rel)
 $units=@(@{name='unit';assembly=$asm;object=$rel;language='c'})
 if($Second){$asm2=Join-Path $dir 'second.asm';$rel2=Join-Path $dir 'second.rel';Save $asm2 $Second;Save $rel2 (Rel);$units+=@{name='second';assembly=$asm2;object=$rel2;language='asm'}}
 $extras=@();if($Extra){$extraPath=Join-Path $dir 'extra.asm';Save $extraPath $Extra;$extras=@($extraPath)}
 $libs=@();if($Library){
  $payload=[Text.Encoding]::ASCII.GetBytes((Rel)+"`n")
  $header=('member.rel/'.PadRight(16)+'0'.PadRight(12)+'0'.PadRight(6)+'0'.PadRight(6)+'100644'.PadRight(8)+$payload.Length.ToString().PadRight(10)+'`'+"`n")
  $data=[Collections.Generic.List[byte]]::new();$data.AddRange([Text.Encoding]::ASCII.GetBytes("!<arch>`n"+$header));$data.AddRange($payload);if($payload.Length%2){$data.Add(10)}
  $lib=Join-Path $dir 'runtime.lib';[IO.File]::WriteAllBytes($lib,$data.ToArray());$libs=@($lib)
 }
 $result=Invoke-Mx251FunctionGc -Root $dir -Units $units -ExtraAssembly $extras -LibraryPaths $libs -KeepSymbols @('_main')
 $m=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable
 if($result.Fallback -ne $Fallback){throw "Fallback mismatch $Name"}
 $u=@($m.units|Where-Object{$_.name -eq 'unit'})[0]
 if(($u.removedUnusedExternalDeclarations -contains '_Start') -ne $ExpectDrop){throw "Import decision mismatch $Name"}
 $text=Get-Content -LiteralPath $u.candidate -Raw
 if(($text -match '(?m)^\s*\.globl[^\r\n]*\b_Start\b') -eq $ExpectDrop){throw "Import text mismatch $Name"}
 if($Fallback -and $u.inputSha256 -cne $u.candidateSha256){throw "Fallback not exact $Name"}
 $results.Add(@{name=$Name;passed=$true;fallback=$Fallback;importRemoved=$ExpectDrop;manifest=$result.Manifest})
}
# A declaration with no owned definition may force archive extraction or GSINIT.
# The closed graph may remove owned definitions, never infer external imports dead.
Case -Name 'dead-only-external-import-is-conservatively-preserved'
Case -Name 'live-call-keeps-import' -Main "ecall _Start`neret"
Case -Name 'live-address-keeps-import' -Main "mov dptr,#_Start`neret"
Case -Name 'const-table-keeps-import' -After ".area CONST (CODE)`n.dw _Start"
Case -Name 'alias-keeps-import' -After '_saved = _Start'
Case -Name 'asm-declaration-keeps-import' -Extra '.globl _Start'
Case -Name 'asm-call-keeps-import' -Extra ".globl _Start`necall _Start"
Case -Name 'archive-reference-keeps-import' -Library $true
Case -Name 'retained-data-definition-keeps-export' -After ".area DSEG (DATA)`n_Start:`n.ds 1"
Case -Name 'opaque-fallback-keeps-import' -Fallback $true
Case -Name 'other-unit-data-keeps-import' -Second ".module other`n.globl _Start`n.area CONST (CODE)`n.dw _Start"
@{status='PASS_UNUSED_IMPORT_CLOSURE_FIXTURES_ONLY';engineSha256=(Get-Mx251GcHash $Engine);cases=@($results.ToArray());hardwareAccess=$false;compilerInvoked=$false;scope='Synthetic graph evidence; ordinary reassembly/linker still determines final relocation and unresolved-symbol validity.'}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) external-import fixtures; $OutputDirectory"
