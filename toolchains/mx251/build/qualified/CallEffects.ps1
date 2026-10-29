# 审计元数据不参与优化；未知、旧后端和非叶函数不能由空摘要推断无破坏。
function ConvertFrom-Mx251EffectsLine([string]$Line,[string]$Kind) {
 $prefix=if($Kind -eq 'function'){'; mx251-effects '}else{'; mx251-call-effects '}
 if(-not $Line.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Invalid effects prefix'}
 $row=[ordered]@{}
 foreach($part in $Line.Substring($prefix.Length).Trim().Split(' ',[StringSplitOptions]::RemoveEmptyEntries)) {
  $pair=$part.Split('=',2)
  if($pair.Count -ne 2 -or $row.Contains($pair[0])){throw 'Malformed or duplicate effects field'}
  $row[$pair[0]]=$pair[1]
 }
 $fields=if($Kind -eq 'function'){@('schema','function','abi','complete','reads','writes','specialReads','specialWrites','flagsReads','flagsWrites','memory','instructions','unknown','calls')}else{@('schema','caller','site','callee','abi','live','setup','finish','complete','saved')}
 if($row.Count -ne $fields.Count -or @($fields|Where-Object{-not $row.Contains($_)}).Count){throw 'Effects schema field mismatch'}
 if($row.schema -cne '1' -or $row.abi -notmatch '^(2|4)$' -or $row.complete -notmatch '^[01]$'){throw 'Unsupported effects contract'}
 foreach($key in $fields) {
  if($key -in @('function','caller','callee')){if($row[$key] -notmatch '^[A-Za-z_.$][A-Za-z_0-9.$]*$'){throw 'Invalid effects symbol'}}
  elseif($key -in @('reads','writes','specialReads','specialWrites','flagsReads','flagsWrites','live','setup','finish')){if($row[$key] -notmatch '^[0-9a-f]{8}$'){throw 'Invalid effects mask'}}
  elseif($key -eq 'saved'){if($row[$key] -notmatch '^([0-9a-f]{8}(,[0-9a-f]{8})*)?$'){throw 'Invalid saved groups'}}
  else{if($row[$key] -notmatch '^\d{1,10}$'){throw 'Invalid effects counter'}}
 }
 if($Kind -eq 'function' -and (($row.complete -eq '1') -ne ($row.unknown -eq '0'))){throw 'Inconsistent complete effects marker'}
 return $row
}

function New-Mx251CallEffects([string]$OutputRoot,$Config,$Receipt) {
 $units=@()
 foreach($unit in $Config.units) {
  $asmName=if([IO.Path]::GetExtension($unit.file) -eq '.c'){$unit.name+'.asm'}else{$unit.file}
  $names=@($asmName,($unit.name+'.rel'));$artifacts=@()
  foreach($name in $names){$p=Join-Path $OutputRoot $name;$artifacts+=@{path=$name;sha256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}}
  $functions=@();$calls=@()
  foreach($line in [IO.File]::ReadAllLines((Join-Path $OutputRoot $asmName))) {
   if($line.StartsWith('; mx251-effects ')){$functions+=ConvertFrom-Mx251EffectsLine $line 'function'}
   elseif($line.StartsWith('; mx251-call-effects ')){$calls+=ConvertFrom-Mx251EffectsLine $line 'call'}
  }
  $units+=@{unit=$unit.name;artifacts=$artifacts;functions=$functions;calls=$calls}
 }
 return [ordered]@{
  schema=1;mode='audit-only';optimizationEnabled=$false
  semantics='may-read/may-write upper bound; not net clobber or proven removable saves'
  coverage='accepted wide functions only; missing/unknown/calls/tail jumps remain conservative'
  compilerSha256=$Receipt.compilerSha256;profileLockSha256=$Receipt.profileLockSha256
  projectSnapshotSha256=$Receipt.projectSnapshotSha256;firmwareSha256=$Receipt.firmwareSha256
  inputs=$Receipt.inputs;units=$units
 }
}

# 研究工具读取审计结果必须核对同次构建收据和对象，不能复用陈旧 sidecar。
function Read-Mx251CallEffects([string]$OutputRoot) {
 $receipt=Get-Content -LiteralPath (Join-Path $OutputRoot 'receipt.json') -Raw | ConvertFrom-Json -AsHashtable
 $path=Join-Path $OutputRoot 'call-effects.json'
 $sha={param($p) (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
 if($receipt.status -cne 'complete' -or (& $sha $path) -cne $receipt.callEffects.sha256){throw 'Effects receipt/hash mismatch'}
 $data=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
 if($data.schema -ne 1 -or $data.mode -cne 'audit-only' -or $data.optimizationEnabled -ne $false){throw 'Unsupported effects document'}
 foreach($field in @('compilerSha256','profileLockSha256','projectSnapshotSha256','firmwareSha256')){if($data[$field] -cne $receipt[$field]){throw "Effects identity mismatch: $field"}}
 foreach($entry in @(@{path='project.snapshot.json';sha256=$data.projectSnapshotSha256},@{path='firmware.hex';sha256=$data.firmwareSha256})+@($data.units|ForEach-Object{$_.artifacts})+@($data.inputs.GetEnumerator()|ForEach-Object{@{path=$_.Key;sha256=$_.Value.sha256}})) {
  $p=[IO.Path]::GetFullPath((Join-Path $OutputRoot $entry.path));$root=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')+'\'
  if(-not $p.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or (& $sha $p) -cne $entry.sha256){throw "Stale or outside effects artifact: $($entry.path)"}
 }
 return $data
}
