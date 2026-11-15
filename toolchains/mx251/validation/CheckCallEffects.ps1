#requires -Version 7.0
param([Parameter(Mandatory)][string]$BuildOutput,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/qualified/CallEffects.ps1')
$root=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $root){throw 'Evidence output must be new'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$source=[IO.Path]::GetFullPath($BuildOutput)
$data=Read-Mx251CallEffects $source
$names=@('receipt.json','call-effects.json','project.snapshot.json','firmware.hex')+@($data.units|ForEach-Object{$_.artifacts|ForEach-Object{$_.path}})+@($data.inputs.Keys)
foreach($name in ($names|Sort-Object -Unique)) {
 $p=[IO.Path]::GetFullPath((Join-Path $root $name))
 if(-not $p.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Outside test copy'}
 [IO.Directory]::CreateDirectory((Split-Path -Parent $p))|Out-Null
 [IO.File]::Copy((Join-Path $source $name),$p,$false)
}
$checks=[Collections.Generic.List[object]]::new()
$null=Read-Mx251CallEffects $root;$checks.Add(@{case='same-build-read';passed=$true})
function Reject([string]$Name,[scriptblock]$Action) {
 $rejected=$false
 try{& $Action|Out-Null}catch{$rejected=$true}
 if(-not $rejected){throw "Negative case was accepted: $Name"}
 $checks.Add(@{case=$Name;passed=$true})
}
foreach($field in @('call-effects.json','receipt.json','project.snapshot.json','firmware.hex', $data.units[0].artifacts[0].path,$data.units[0].artifacts[1].path, @($data.inputs.Keys)[0])) {
 $path=Join-Path $root $field;$original=[IO.File]::ReadAllBytes($path)
 try {
  [IO.File]::WriteAllBytes($path,($original+[byte]32))
  # receipt JSON 尾空白不改变语义，需改身份；其他文件即便仅空白也应因哈希拒绝。
  if($field -eq 'receipt.json'){
   $r=[Text.Encoding]::UTF8.GetString($original)|ConvertFrom-Json -AsHashtable
   $r.compilerSha256='0'*64;[IO.File]::WriteAllText($path,($r|ConvertTo-Json -Depth 30))
  }
  Reject ('stale-'+$field) {Read-Mx251CallEffects $root}
 } finally {[IO.File]::WriteAllBytes($path,$original)}
}
$functionLine='; mx251-effects schema=1 function=_leaf abi=4 complete=1 reads=00000001 writes=00000002 specialReads=00000000 specialWrites=00000000 flagsReads=00000000 flagsWrites=00000000 memory=0 instructions=1 unknown=0 calls=0'
$null=ConvertFrom-Mx251EffectsLine $functionLine 'function'
$checks.Add(@{case='parse-valid-function';passed=$true})
$badLines=@(($functionLine+' abi=4'),$functionLine.Replace('schema=1','schema=2'),$functionLine.Replace('unknown=0','unknown=1'),$functionLine.Replace('writes=00000002','writes=oops'),$functionLine.Replace('function=_leaf','function=../leaf'))
if($badLines.Count -ne 5){throw 'Negative schema cases missing'}
foreach($line in $badLines){Reject 'malformed-function' {ConvertFrom-Mx251EffectsLine $line 'function'}}
$call='; mx251-call-effects schema=1 caller=_caller site=9 callee=_leaf abi=4 live=0000000f setup=00000000 finish=00000000 complete=1 saved=0000000f'
$null=ConvertFrom-Mx251EffectsLine $call 'call';$null=ConvertFrom-Mx251EffectsLine ($call.Replace('saved=0000000f','saved=')) 'call'
$checks.Add(@{case='parse-call-and-empty-saves';passed=$true})
Reject 'malformed-saved-group' {ConvertFrom-Mx251EffectsLine ($call.Replace('saved=0000000f','saved=bad')) 'call'}
$null=Read-Mx251CallEffects $root
$checks.Add(@{case='restored-test-copy';passed=$true})
[IO.File]::WriteAllText((Join-Path $root 'checks.json'),(ConvertTo-Json -Depth 5 -InputObject @($checks)))
Write-Output "Call effects: $($checks.Count) integrity/schema checks passed; original build preserved."
