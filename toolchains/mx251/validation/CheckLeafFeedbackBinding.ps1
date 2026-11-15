#requires -Version 7.0
param([Parameter(Mandatory)][string]$BuildOutput,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/qualified/CallEffects.ps1')
. (Join-Path $PSScriptRoot '../build/qualified/LeafCallFeedback.ps1')
function Hash-File([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
$root=[IO.Path]::GetFullPath($Output);if(Test-Path -LiteralPath $root){throw 'Test output must be new'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$source=[IO.Path]::GetFullPath($BuildOutput)
$record=Get-Content -LiteralPath (Join-Path $source 'leaf-feedback.json') -Raw|ConvertFrom-Json -AsHashtable
$config=Get-Content -LiteralPath (Join-Path $source 'project.snapshot.json') -Raw|ConvertFrom-Json -AsHashtable
if(-not $record.finalValidated -or -not $record.leaves.Count){throw 'A completed feedback build with leaves is required'}
$names=@('leaf-effects.txt')+@($record.inputs.Keys)+@($record.pass1|ForEach-Object{'feedback-pass1/'+$_.path})+@($config.units|ForEach-Object{if([IO.Path]::GetExtension($_.file)-eq '.c'){$_.name+'.asm'}else{$_.file}})
foreach($name in ($names|Sort-Object -Unique)) {
 $dest=[IO.Path]::GetFullPath((Join-Path $root $name));if(-not $dest.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Outside test path'}
 [IO.Directory]::CreateDirectory((Split-Path -Parent $dest))|Out-Null;[IO.File]::Copy((Join-Path $source $name),$dest,$false)
}
$checks=[Collections.Generic.List[object]]::new()
function Valid {Assert-Mx251LeafFeedback $root $config $record $record.compilerSha256 $record.profileLockSha256}
function Reject([string]$Name,[scriptblock]$Action){$rejected=$false;try{&$Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw "Failed rejection: $Name"};$checks.Add(@{case=$Name;passed=$true})}
Valid;$checks.Add(@{case='same-build-feedback';passed=$true})
foreach($name in @('leaf-effects.txt',('feedback-pass1/'+$record.pass1[0].path),@($record.inputs.Keys)[0])) {
 $path=Join-Path $root $name;$original=[IO.File]::ReadAllBytes($path)
 try{[IO.File]::WriteAllBytes($path,($original+[byte]32));Reject ('changed-'+$name) {Valid}}finally{[IO.File]::WriteAllBytes($path,$original)}
}
Reject 'different-compiler' {Assert-Mx251LeafFeedback $root $config $record ('0'*64) $record.profileLockSha256}
Reject 'different-profile' {Assert-Mx251LeafFeedback $root $config $record $record.compilerSha256 ('0'*64)}
$leaf=$record.leaves[0];$path=Join-Path $root $leaf.assembly;$original=[IO.File]::ReadAllText($path)
try {
 $old=$leaf.symbol+":";$new=$old+"`n`tnop"
 [IO.File]::WriteAllText($path,$original.Replace($old,$new));Reject 'callee-body-changed-after-feedback' {Valid}
 [IO.File]::WriteAllText($path,$original+"`n"+$leaf.body+"; mx251-effects schema=1 function="+$leaf.symbol+" abi="+$leaf.abi+" complete=1 reads=00000000 writes="+$leaf.writes+" specialReads=00000000 specialWrites=00000000 flagsReads=00000000 flagsWrites=00000000 memory=0 instructions=1 unknown=0 calls=0`n")
 Reject 'ambiguous-final-definition' {Valid}
}finally{[IO.File]::WriteAllText($path,$original)}
Valid;$checks.Add(@{case='restored-copy';passed=$true})
[IO.File]::WriteAllText((Join-Path $root 'result.json'),(ConvertTo-Json -Depth 5 -InputObject @($checks)))
Write-Output "Leaf feedback source/object binding checks passed: $($checks.Count)"
