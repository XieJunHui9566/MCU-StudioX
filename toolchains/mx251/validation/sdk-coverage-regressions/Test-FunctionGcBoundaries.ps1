#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. $Engine
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Save([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function Fn([string]$Name,[string]$Body){return "; mx251-function begin schema=1 symbol=$Name isr=0 addressTaken=0 opaque=0 indirect=0`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"}
function Rel([string[]]$Refs=@()){return "XH3`nH 1 areas $($Refs.Count.ToString('X')) global symbols`nM test`n"+((@($Refs|ForEach-Object{"S $_ Ref000000"})) -join "`n")+"`nA CSEG size 0 flags 20 addr 0"}
function Case([string]$Name,[string]$First,[string]$Second,[string]$Extra,[string[]]$LibraryRefs,[string[]]$Removed,[bool]$Fallback=$false){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $units=@();$i=0
 foreach($asmText in @($First,$Second)) {if(-not $asmText){continue};$asm=Join-Path $dir "u$i.asm";$rel=Join-Path $dir "u$i.rel";Save $asm $asmText;Save $rel (Rel);$units+=@{name="u$i";assembly=$asm;object=$rel;language='c'};$i++}
 $extras=@();if($Extra){$extraPath=Join-Path $dir 'extra.asm';Save $extraPath $Extra;$extras=@($extraPath)}
 $libs=@();if($LibraryRefs.Count){
  $payload=[Text.Encoding]::ASCII.GetBytes((Rel $LibraryRefs)+"`n")
  $header=('member.rel/'.PadRight(16)+'0'.PadRight(12)+'0'.PadRight(6)+'0'.PadRight(6)+'100644'.PadRight(8)+$payload.Length.ToString().PadRight(10)+'`'+"`n")
  $data=[Collections.Generic.List[byte]]::new();$data.AddRange([Text.Encoding]::ASCII.GetBytes("!<arch>`n"+$header));$data.AddRange($payload);if($payload.Length%2){$data.Add(10)}
  $lib=Join-Path $dir 'runtime.lib';[IO.File]::WriteAllBytes($lib,$data.ToArray());$libs=@($lib)
 }
 $result=Invoke-Mx251FunctionGc -Root $dir -Units $units -ExtraAssembly $extras -LibraryPaths $libs -KeepSymbols @('_main')
 $manifest=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable
 if($result.Fallback -ne $Fallback -or (@($result.RemovedFunctions|Sort-Object)-join',') -cne (@($Removed|Sort-Object)-join',')){throw "FAILED $Name $(ConvertTo-Json -InputObject $manifest -Depth 20 -Compress)"}
 foreach($unit in $manifest.units){if($Fallback -and $unit.inputSha256 -cne $unit.candidateSha256){throw "Non-exact fallback: $Name"}}
 $results.Add(@{name=$Name;passed=$true;removed=@($result.RemovedFunctions);fallback=$result.Fallback;manifest=$result.Manifest})
}
$prefix=".module test`n.globl _main,_used,_dead`n.area CSEG (CODE)"
$main=Fn '_main' 'eret';$used=Fn '_used' 'eret';$dead=Fn '_dead' "_secondary:`neret"
Case 'asm-declaration-root' "$prefix`n$main`n$dead" '' '.globl _dead' @() @()
Case 'library-entry-root' "$prefix`n$main`n$dead" '' '' @('_dead') @()
Case 'library-secondary-root' "$prefix`n$main`n$dead" '' '' @('_secondary') @()
Case 'cross-unit-secondary-label' "$prefix`n$(Fn '_main' 'ecall _secondary')" ".module second`n.area CSEG (CODE)`n$dead" '' @() @()
Case 'cross-unit-ambiguous-label-fallback' "$prefix`n$(Fn '_main' 'ecall _secondary')" ".module second`n.area CSEG (CODE)`n$dead`n$(Fn '_used' "_secondary:`neret")" '' @() @() $true
Case 'external-asm-import' "$prefix`n$main`n$dead" '' ".globl s_CSEG`nmov dptr,#s_CSEG" @() @('_dead')
Case 'arithmetic-control-fallback' "$prefix`n$(Fn '_main' 'ecall (0x123+2)')`n$dead" '' '' @() @() $true
Case 'function-offset-fallback' "$prefix`n$main`n$dead`n.area CONST (CODE)`n.dw _dead+256" '' '' @() @() $true
@{status='PASS_BOUNDARIES_ONLY';cases=@($results);hardwareAccess=$false;compilerInvoked=$false}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) boundary fixtures; $OutputDirectory"
