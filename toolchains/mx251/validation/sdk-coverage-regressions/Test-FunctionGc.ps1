#requires -Version 7.0
param([Parameter(Mandatory)][string]$Engine,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. $Engine
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('run-'+[Guid]::NewGuid().ToString('N'))}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$results=[Collections.Generic.List[object]]::new()
function Assert-Gc($Condition,[string]$Why){if(-not $Condition){throw "ASSERT: $Why"}}
function Save-GcText($Path,$Text){[IO.File]::WriteAllText($Path,$Text.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))}
function New-GcFunction($Name,$Body,[string]$Flags='isr=0 addressTaken=0 opaque=0 indirect=0'){
 return "; mx251-function begin schema=1 symbol=$Name $Flags`n${Name}:`n$Body`n; mx251-function end schema=1 symbol=$Name"
}
function Test-GcCase([string]$Name,[string]$Assembly,[string[]]$ExpectedRemoved,[string[]]$RelRefs=@(),[switch]$Fallback,[string[]]$Keep=@('_main'),[string]$Extra=''){
 $dir=Join-Path $OutputDirectory $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $asm=Join-Path $dir 'unit.asm';$rel=Join-Path $dir 'unit.rel'
 Save-GcText $asm $Assembly
 $relText="XH3`nH 1 areas $($RelRefs.Count.ToString('X')) global symbols`nM test`n"+((@($RelRefs|ForEach-Object{"S $_ Ref000000"})) -join "`n")+"`nA CSEG size 0 flags 20 addr 0"
 Save-GcText $rel $relText
 $extras=@();if($Extra){$extraPath=Join-Path $dir 'extra.asm';Save-GcText $extraPath $Extra;$extras=@($extraPath)}
 $result=Invoke-Mx251FunctionGc -Root $dir -Units @(@{name='unit';assembly=$asm;object=$rel;language='c'}) -ExtraAssembly $extras -KeepSymbols $Keep
 $manifest=Get-Content -LiteralPath $result.Manifest -Raw|ConvertFrom-Json -AsHashtable
 Assert-Gc ($result.Fallback -eq $Fallback.IsPresent) "$Name fallback $(ConvertTo-Json -InputObject $manifest.issues -Compress)"
 Assert-Gc ((@($result.RemovedFunctions|Sort-Object) -join ',') -ceq (@($ExpectedRemoved|Sort-Object) -join ',')) "$Name removed set"
 $candidate=Join-Path $dir 'function-gc/candidate/unit.asm'
 if($Fallback){Assert-Gc ((Get-Mx251GcHash $candidate) -ceq (Get-Mx251GcHash $asm)) "$Name exact fallback"}
 $results.Add(@{name=$Name;passed=$true;fallback=$result.Fallback;removed=@($result.RemovedFunctions);manifest=$result.Manifest})
 return @{candidate=$candidate;manifest=$manifest}
}
$main=New-GcFunction '_main' "`tecall _used`n`teret"
$used=New-GcFunction '_used' "`teret"
$dead=New-GcFunction '_dead' "`tecall 00100`$`n`teret`n00100`$:`n`tmov a,#1`n`teret"
$prefix=".module test`n.globl _main, _used, _dead`n.area CSEG (CODE)"
$r=Test-GcCase 'direct-plus-outline' "$prefix`n$main`n$used`n$dead" @('_dead')
$text=Get-Content -LiteralPath $r.candidate -Raw
Assert-Gc (-not $text.Contains('00100$')) 'dead outlined helper removed with owner'
Assert-Gc (-not $text.Contains('.globl _main, _used, _dead')) 'dead export declaration removed'
$null=Test-GcCase 'data-pointer-root' "$prefix`n$main`n$used`n$dead`n.area CONST (CODE)`n_table:`n.dw _dead" @()
$null=Test-GcCase 'address-taken-root' "$prefix`n$main`n$used`n$(New-GcFunction '_dead' 'eret' 'isr=0 addressTaken=1 opaque=0 indirect=0')" @()
$null=Test-GcCase 'isr-root' "$prefix`n$main`n$used`n$(New-GcFunction '_dead' 'reti' 'isr=1 addressTaken=0 opaque=0 indirect=0')" @()
$null=Test-GcCase 'symbol-alias-root' "$prefix`n$main`n$used`n$dead`n_handler = _dead" @()
$null=Test-GcCase 'extra-assembly-root' "$prefix`n$main`n$used`n$dead" @() -Extra ".area HOME (CODE)`nejmp _dead"
$null=Test-GcCase 'indirect-fallback' "$prefix`n$(New-GcFunction '_main' 'eret' 'isr=0 addressTaken=0 opaque=0 indirect=1')`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'opaque-fallback' "$prefix`n$(New-GcFunction '_main' 'eret' 'isr=0 addressTaken=0 opaque=1 indirect=0')`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'missing-metadata-fallback' "$prefix`n_main:`neret`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'unknown-instruction-fallback' "$prefix`n$(New-GcFunction '_main' 'secret-op r0')`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'unknown-rel-reference-fallback' "$prefix`n$main`n$used`n$dead" @() -RelRefs @('_hidden_address') -Fallback
$null=Test-GcCase 'unknown-asm-reference-fallback' "$prefix`n$(New-GcFunction '_main' 'ecall _missing')`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'absolute-call-fallback' "$prefix`n$(New-GcFunction '_main' 'ecall 0x123456')`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'mixed-area-fallback' "$prefix`n$(New-GcFunction '_main' ".area CONST (CODE)`n.db 0`n.area CSEG (CODE)`neret")`n$used`n$dead" @() -Fallback
$null=Test-GcCase 'unclosed-marker-fallback' "$prefix`n$main`n$used`n; mx251-function begin schema=1 symbol=_dead isr=0 addressTaken=0 opaque=0 indirect=0`n_dead:`neret" @() -Fallback
$null=Test-GcCase 'local-labels-independent' "$prefix`n$(New-GcFunction '_main' 'eret')`n$(New-GcFunction '_used' "00100`$:`nsjmp 00100`$")`n$dead" @('_used','_dead')
$out=@{status='PASS_PARSER_AND_CLOSURE_FIXTURES_ONLY';cases=@($results.ToArray());hardwareAccess=$false;compilerInvoked=$false;limitation='Synthetic ASM/REL fixtures verify safe graph decisions, not instruction execution or final linker output.'}
$out|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
Write-Output "PASS $($results.Count) GC fixtures; $OutputDirectory"
