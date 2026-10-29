#requires -Version 7.0
param(
 [Parameter(Mandatory=$true)][string]$Project,
 [Parameter(Mandatory=$true)][string]$ToolchainRoot,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [Parameter(Mandatory=$true)][string]$ProfileRoot,
 [string]$CMakeInput
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$commands=[Collections.Generic.List[object]]::new()
$ownsOutput=$false
$outputRoot=$null
$includeDirectories=@()
function Write-Json($Path,$Value) {
 [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 50)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Keys-Only($Object,[string[]]$Allowed,[string]$Label) {
 if($Object -isnot [Collections.IDictionary]) {throw "$Label must be an object"}
 foreach($key in $Object.Keys) {if($key -notin $Allowed){throw "Unknown $Label property: $key"}}
}
function Choice($Value,[string[]]$Allowed,[string]$Label) {
 if($Value -isnot [string] -or $Value -cnotin $Allowed){throw "Unsupported ${Label}: $Value"}
}
function Relative-File([string]$Root,$Relative) {
 if($Relative -isnot [string] -or [string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.?([\\/]|$)' -or $Relative.Contains(':')) {throw "Invalid relative path: $Relative"}
 $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
 if(-not $full.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes its declared root'}
 $item=Get-Item -LiteralPath $full -Force
 if($item -isnot [IO.FileInfo]){throw "Expected file: $Relative"}
 $ancestor=$item
 while($null -ne $ancestor -and $ancestor.FullName -ne $Root) {
  if($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point is outside this qualified build contract: $Relative"}
  $ancestor=if($ancestor -is [IO.DirectoryInfo]){$ancestor.Parent}else{$ancestor.Directory}
 }
 return $full
}
function Defines($Values) {
 if($Values -isnot [array]){throw 'defines must be an array'}
 foreach($value in $Values){if($value -isnot [string] -or $value -notmatch '^[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_+.-]+)?$'){throw "Unsupported define: $value"}}
}
function Run-Tool([string]$Label,[string]$Exe,[string[]]$Arguments) {
 $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$Exe;$start.WorkingDirectory=$outputRoot
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 # 子进程只使用锁定工具；用户环境保持原样，避免环境变量暗中替换头文件或运行库。
 foreach($key in @('SDCC_HOME','SDCC_INCLUDE','SDCC_LIB','SDCC_BIN','SDCCLIB','SDCCDIR','CPATH','C_INCLUDE_PATH')){$start.Environment.Remove($key)|Out-Null}
 $start.Environment['PATH']=(Join-Path $toolRoot 'bin')+';'+(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot
 foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::new();$process.StartInfo=$start
 $stdout=[IO.File]::Create((Join-Path $outputRoot ($Label+'.stdout.log')))
 $stderr=[IO.File]::Create((Join-Path $outputRoot ($Label+'.stderr.log')))
 try {
  if(-not $process.Start()){throw "Cannot start $Label"}
  $readOut=$process.StandardOutput.BaseStream.CopyToAsync($stdout);$readErr=$process.StandardError.BaseStream.CopyToAsync($stderr)
  if(-not $process.WaitForExit(120000)){$process.Kill($true);$process.WaitForExit();throw "Tool timeout: $Label"}
  [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($readOut,$readErr))
  $code=$process.ExitCode
  $commands.Add([ordered]@{label=$Label;executable=$Exe;arguments=$Arguments;exitCode=$code})
  Write-Json (Join-Path $outputRoot 'commands.json') @($commands.ToArray())
  if($code -ne 0){throw "$Label failed with exit code $code; original stdout/stderr logs retained"}
 } finally {$stdout.Dispose();$stderr.Dispose();$process.Dispose()}
}
function Flags([string]$Backend,[bool]$StackAuto,$ExtraDefines) {
 $f=@('-mmcs251',('--model-'+$config.model),('--opt-code-'+$config.optimization))
 if($config.standard -ne 'default'){$f+=('--std-'+$config.standard)}
 if($config.explicitAbi){$f+='--mcs251-native-abi'}
 if($StackAuto){$f+='--stack-auto'}
 if($Backend -eq 'ordinary-wide'){$f+='--mcs251-wide-ordinary'}
 if($Backend -eq 'explicit-wide'){$f+='--mcs251-wide-backend'}
 if($runtime -in @('native-int','native-int-mdu32')){$f+='--mcs251-native-int-runtime'}
 if($runtime -in @('native-math','native-math-wide','native-math-mdu32','native-math-wide-mdu32')){$f+=@('--mcs251-native-runtime','--mcs251-native-int-runtime','--nostdlib')}
 foreach($d in @($config.defines)+@($ExtraDefines)){$f+=('-D'+$d)}
 foreach($directory in $includeDirectories){$f+=('-I'+$directory)}
 return $f
}
function Check-Assembly([string]$Path,[bool]$QualifiedStartup=$false) {
 # 已知汇编边界：未解析表达式会影响DR立即数扩展编码。尚未修复前明确拒绝，不能静默发错码。
 $lineNumber=0
 foreach($line in [IO.File]::ReadAllLines($Path)){
  $lineNumber++
  $code=($line -split ';',2)[0]
  $match=[regex]::Match($code,'(?i)^\s*(?:[\w.$]+:\s*)?(mov|cmp)\s+(dr\d+|dpx|spx)\s*,\s*#(.+?)\s*$')
  if(-not $match.Success){continue}
  $expr=$match.Groups[3].Value
  if($expr -match '^[+-]?(0[xX][0-9a-fA-F]+|[0-9]+)$'){continue}
  if($QualifiedStartup -and $match.Groups[1].Value -ieq 'mov' -and $match.Groups[2].Value -ieq 'spx' -and $expr -ceq '__mcs251_stack_initial'){continue}
  throw "Unqualified symbolic DR immediate at ${Path}:${lineNumber}: $expr; use explicit byte/word loads. No firmware published."
 }
}
try {
 $projectPath=(Resolve-Path -LiteralPath $Project).Path
 $projectRoot=Split-Path -Parent $projectPath
 $toolRoot=(Resolve-Path -LiteralPath $ToolchainRoot).Path.TrimEnd('\')
 $config=Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
 # 工程管理适配层：编译器、运行库和启动来自当前组件锁定的 profile。
 $ProfileRoot=(Resolve-Path -LiteralPath $ProfileRoot).Path
 if($CMakeInput){
  $spec=Get-Content -LiteralPath $CMakeInput -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
  Keys-Only $spec @('sources','includeDirectories','defines') 'CMake inputs'
  Defines $spec.defines
  if(@($spec.defines|Where-Object{$_ -match '^MX251_(IRC_HZ|DELAY_TICKS)(=|$)'}).Count){throw 'Set the IRC clock in project settings, not CMake overrides'}
  $config.defines=@($config.defines)+@($spec.defines)
  $previous=@{};if($config.ContainsKey('units')){foreach($u in $config.units){$previous[$u.file.Replace('\','/')]=$u}}
  $newUnits=@();$newFiles=@();$newArchives=[ordered]@{};$newLink=@()
  foreach($source in $spec.sources){
   Keys-Only $source @('file','MX251_BACKEND','MX251_ARCHIVE','defines') 'CMake source'
   $relative=[IO.Path]::GetRelativePath($projectRoot,[IO.Path]::GetFullPath($source.file)).Replace('\','/')
   $null=Relative-File $projectRoot $relative
   if($relative -in $newFiles){throw "Duplicate source: $relative"};$newFiles+=$relative
   if([IO.Path]::GetExtension($relative) -in @('.h','.inc')){continue}
   if($previous.ContainsKey($relative)){$unit=$previous[$relative].Clone()}
   else{$unit=@{name=('u'+$newUnits.Count);file=$relative;backend=$config.backend;stackAuto=$true;defines=@()}}
   if($source.ContainsKey('MX251_BACKEND')){$unit.backend=$source.MX251_BACKEND}
   if($source.ContainsKey('defines')){
    Defines $source.defines
    if(@($source.defines|Where-Object{$_ -match '^MX251_(IRC_HZ|DELAY_TICKS)(=|$)'}).Count){throw 'Per-source clock overrides are unsupported'}
    $unit.defines=@($unit.defines)+@($source.defines)
   }
   $newUnits+=$unit
   $archive=if($source.ContainsKey('MX251_ARCHIVE')){$source.MX251_ARCHIVE}else{$null}
   if($archive){
    if(-not $newArchives.Contains($archive)){$newArchives[$archive]=@();$newLink+=$archive}
    $newArchives[$archive]+=$unit.name
   }else{$newLink+=$unit.name}
  }
  $config.units=$newUnits;$config.files=$newFiles
  $config.archives=@(foreach($name in $newArchives.Keys){@{name=$name;units=@($newArchives[$name])}})
  $config.link=$newLink+@('@startup')
  $includeDirectories=@($spec.includeDirectories)
 }elseif(Test-Path -LiteralPath (Join-Path $projectRoot 'include') -PathType Container){$includeDirectories=@('include')}
 $normalizedIncludes=@()
 foreach($directory in $includeDirectories){
  $full=[IO.Path]::GetFullPath($directory,$projectRoot)
  $relative=[IO.Path]::GetRelativePath($projectRoot,$full).Replace('\','/')
  if($relative -eq '.' -or $relative -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($relative) -or -not (Test-Path -LiteralPath $full -PathType Container)){throw "Include directory must be inside the project: $directory"}
  $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($full)
  while($pending.Count){
   $dir=$pending.Pop();if((Get-Item -LiteralPath $dir).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked include directory rejected'}
   foreach($entry in Get-ChildItem -LiteralPath $dir -Force){
    if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked include input rejected'}
    if($entry.PSIsContainer){$pending.Push($entry.FullName)}
    elseif($entry.Extension -in @('.h','.inc')){
     $file=[IO.Path]::GetRelativePath($projectRoot,$entry.FullName).Replace('\','/')
     if($file -notin $config.files){$config.files+=@($file)}
    }
   }
  }
  $normalizedIncludes+=$relative
 }
 $includeDirectories=$normalizedIncludes
 $commonKeys=@('schema','target','model','optimization','backend','explicitAbi','standard','defines','files','units','archives','link')
 if($config.schema -eq 1){
  Keys-Only $config ($commonKeys+@('nativeMath')) 'project'
  if(-not $config.ContainsKey('nativeMath') -or $config.nativeMath -isnot [bool]){throw 'nativeMath must be boolean in schema 1'}
  $runtime=if($config.nativeMath){'native-math'}else{'standard'}
 } elseif($config.schema -eq 2){
  Keys-Only $config ($commonKeys+@('runtime')) 'project'
  if(-not $config.ContainsKey('runtime')){throw 'Missing project property: runtime'}
  Choice $config.runtime @('standard','native-int','native-math','native-math-wide','native-int-mdu32','native-math-mdu32','native-math-wide-mdu32') 'runtime'
  $runtime=$config.runtime
 } else{throw 'Unsupported project schema'}
 foreach($key in $commonKeys){if(-not $config.ContainsKey($key)){throw "Missing project property: $key"}}
 Choice $config.target @('AI8051U-34K64-32') 'target'
 Choice $config.model @('small','large') 'model'
 Choice $config.optimization @('speed','size') 'optimization'
 Choice $config.backend @('legacy','ordinary-wide','explicit-wide') 'backend'
 Choice $config.standard @('default','sdcc11') 'standard'
 if($config.explicitAbi -isnot [bool]){throw 'explicitAbi must be boolean'}
 if($runtime -notin @('standard','native-int-mdu32') -and $config.model -ne 'small'){throw 'Native runtime is qualified only for small stack-auto'}
 if($runtime -in @('native-math','native-math-mdu32') -and ($config.explicitAbi -or $config.backend -ne 'legacy')){throw 'Native math entry currently qualifies the fixed ordinary ABI full-math project only'}
 if($runtime -in @('native-math-wide','native-math-wide-mdu32') -and (-not $config.explicitAbi -or $config.backend -ne 'explicit-wide')){throw 'native-math-wide requires explicit ABI and explicit-wide backend'}
 if($runtime -eq 'native-int' -and ($config.explicitAbi -or $config.backend -ne 'explicit-wide')){throw 'native-int qualification requires the fixed explicit-wide/default ABI configuration'}
 Defines $config.defines
 foreach($key in @('files','units','archives','link')){if($config[$key] -isnot [array]){throw "$key must be an array"}}
 if(-not $config.units.Count){throw 'No translation units'}
 $inputs=@{};$units=@{};$archives=@{};$coverage=@{}
 foreach($file in $config.files){
  if($inputs.ContainsKey($file)){throw "Duplicate input: $file"}
  if([IO.Path]::GetFileName($file) -ieq 'startup.asm'){throw 'startup.asm is reserved by the qualified profile'}
  if([IO.Path]::GetExtension($file) -cnotin @('.c','.h','.asm','.inc')){throw "Unsupported input type: $file"}
  $path=Relative-File $projectRoot $file
  $inputs[$file]=[ordered]@{path=$path;sha256=(Hash-File $path)}
 }
 foreach($u in $config.units){
  Keys-Only $u @('name','file','backend','stackAuto','defines') 'unit'
  foreach($key in @('name','file','backend','stackAuto','defines')){if(-not $u.ContainsKey($key)){throw "Missing unit property: $key"}}
  if($u.name -isnot [string] -or $u.name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $u.name -ieq 'startup' -or $units.ContainsKey($u.name)){throw 'Duplicate or invalid unit name'}
  if(-not $inputs.ContainsKey($u.file) -or [IO.Path]::GetExtension($u.file) -cnotin @('.c','.asm')){throw "Missing unit source: $($u.file)"}
  Choice $u.backend @('legacy','ordinary-wide','explicit-wide') 'unit backend'
  if($u.stackAuto -isnot [bool]){throw 'stackAuto must be boolean'}
  if(-not $u.stackAuto -and -not $config.explicitAbi){throw 'Non-auto units require the explicit reentrant ABI qualification profile'}
  if($runtime -ne 'standard' -and -not $u.stackAuto){throw 'Native runtime requires stack-auto in every unit'}
  if($runtime -notin @('standard','native-int-mdu32') -and $u.backend -ne $config.backend){throw 'Native runtime unit backend is outside this qualification profile'}
  Defines $u.defines;$units[$u.name]=$u;$coverage[$u.name]=0
 }
 foreach($a in $config.archives){
  Keys-Only $a @('name','units') 'archive'
  if($a.name -isnot [string] -or $a.name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $a.name -ieq 'startup' -or $units.ContainsKey($a.name) -or $archives.ContainsKey($a.name)){throw 'Duplicate or invalid archive name'}
  if($a.units -isnot [array] -or -not $a.units.Count){throw 'Empty or invalid archive'}
  foreach($n in $a.units){if(-not $units.ContainsKey($n)){throw "Unknown archive member: $n"};$coverage[$n]++}
  $archives[$a.name]=$a
 }
 $seen=@{};$linkInputs=@()
 foreach($n in $config.link){
  if($n -isnot [string] -or $seen.ContainsKey($n)){throw 'Duplicate or invalid link entry'};$seen[$n]=$true
  if($n -eq '@startup'){$linkInputs+='startup.rel'}
  elseif($units.ContainsKey($n)){$linkInputs+=($n+'.rel');$coverage[$n]++}
  elseif($archives.ContainsKey($n)){$linkInputs+=($n+'.lib')}
  else{throw "Unknown link input: $n"}
 }
 if(-not $seen.ContainsKey('@startup')){throw 'Qualified startup must be linked exactly once'}
 foreach($n in $coverage.Keys){if($coverage[$n] -ne 1){throw "Unit $n must be placed exactly once"}}
 foreach($n in $archives.Keys){if(-not $seen.ContainsKey($n)){throw "Unused archive declaration: $n"}}
 # 工具、运行库、头文件和启动均按清单核验；不修改系统PATH或IDE默认工具链。
 $lockPath=Join-Path $ProfileRoot 'profile.lock.json';$lock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
 foreach($entry in $lock.toolchain){$path=Relative-File $toolRoot $entry.path;if((Hash-File $path) -ne $entry.sha256){throw "Toolchain lock mismatch: $($entry.path)"}}
 foreach($entry in $lock.profile){$path=Relative-File $ProfileRoot $entry.path;if((Hash-File $path) -ne $entry.sha256){throw "Profile lock mismatch: $($entry.path)"}}
 if($runtime -in @('native-int-mdu32','native-math-mdu32','native-math-wide-mdu32')){
  # 硬件支持由目标能力契约与配套库共同声明；不能只因指令集兼容就给其它芯片写DMAIR。
  if(-not $lock.ContainsKey('mdu32') -or $lock.target -cne $config.target -or $lock.mdu32.target -cne $config.target -or $lock.mdu32.abi -cne 'mcs251-r4' -or $lock.mdu32.trigger -cne 'mov-direct-immediate') {throw 'This target profile has no qualified MDU32 ABI/trigger contract'}
  if(@($lock.profile|Where-Object{$_.path -ceq 'runtime/libai8051umdu32.lib'}).Count -ne 1){throw 'MDU32 runtime must be included exactly once in the profile lock'}
 }
 $outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
 if(Test-Path -LiteralPath $outputRoot){throw 'Output directory already exists; choose a fresh build directory. Existing files are preserved.'}
 if($outputRoot.StartsWith($toolRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $toolRoot -or  $outputRoot.StartsWith($ProfileRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be separate from toolchain and profile'}
 [IO.Directory]::CreateDirectory($outputRoot) | Out-Null;$ownsOutput=$true
 Write-Json (Join-Path $outputRoot 'build-state.json') @{status='building';project=$projectPath;profileLockSha256=(Hash-File $lockPath)}
 Write-Json (Join-Path $outputRoot 'project.snapshot.json') $config
 foreach($file in $inputs.Keys){$dest=Join-Path $outputRoot $file;[IO.Directory]::CreateDirectory((Split-Path -Parent $dest))|Out-Null;[IO.File]::Copy($inputs[$file].path,$dest,$false);if((Hash-File $dest) -ne $inputs[$file].sha256){throw 'Input changed during snapshot'}}
 [IO.File]::Copy((Join-Path $ProfileRoot 'startup.asm'),(Join-Path $outputRoot 'startup.asm'),$false)
 foreach($u in $config.units){
  if([IO.Path]::GetExtension($u.file) -eq '.c'){$args=@(Flags $u.backend $u.stackAuto $u.defines)+@('-c',$u.file,'-o',($u.name+'.rel'));Run-Tool $u.name (Join-Path $toolRoot 'bin/sdcc.exe') $args;Check-Assembly (Join-Path $outputRoot ($u.name+'.asm'))}
  else{Check-Assembly (Join-Path $outputRoot $u.file);Run-Tool $u.name (Join-Path $toolRoot 'bin/sdas251.exe') @('-plosgff',($u.name+'.rel'),$u.file)}
 }
 foreach($a in $config.archives){$args=@('-rc',($a.name+'.lib'))+@($a.units|ForEach-Object{$_+'.rel'});Run-Tool ('archive-'+$a.name) (Join-Path $toolRoot 'bin/sdar.exe') $args}
 Check-Assembly (Join-Path $outputRoot 'startup.asm') $true
 Run-Tool 'startup' (Join-Path $toolRoot 'bin/sdas251.exe') @('-plosgff','startup.rel','startup.asm')
 $flags=@(Flags $config.backend $true @())
 $libs=@()
 if($runtime -eq 'native-int'){$libs+=(Join-Path $ProfileRoot 'runtime/libnativeint.lib')}
 if($runtime -eq 'native-int-mdu32'){$libs+=(Join-Path $ProfileRoot 'runtime/libai8051umdu32.lib')}
 if($runtime -in @('native-math','native-math-wide','native-math-mdu32','native-math-wide-mdu32')){
  $libs+=(Join-Path $ProfileRoot 'runtime/libnativefloat.lib')
  if($runtime -eq 'native-math'){$libs+=(Join-Path $ProfileRoot 'runtime/libnativeint.lib')}
  if($runtime -in @('native-math-mdu32','native-math-wide-mdu32')){$libs+=(Join-Path $ProfileRoot 'runtime/libai8051umdu32.lib')}
  foreach($n in @('mcs251','libsdcc','libint','liblong','liblonglong')){$libs+=(Join-Path $toolRoot ('lib/mcs251-small-stack-auto/'+$n+'.lib'))}
  if($runtime -eq 'native-math-wide'){$libs+=(Join-Path $ProfileRoot 'runtime/libnativeint.lib')}
 }
 $args=$flags+@('--code-loc','0xff0000','--code-size','0x10000','--xram-loc','0x10000','--xram-size','0x7000','--iram-size','0x100','--mcs251-stack','0x400,0x400,0x800')+$linkInputs+$libs+@('-o','linked.partial.hex')
 Run-Tool 'link' (Join-Path $toolRoot 'bin/sdcc.exe') $args
 # 成功退出仍需核对完整HEX和真实SSEG后，才发布最终固件名称。
 $hex=Join-Path $outputRoot 'linked.partial.hex';$memory=@{};$base=0L;$eof=$false
 foreach($line in [IO.File]::ReadAllLines($hex)){
  if($eof -or $line -notmatch '^:([0-9A-Fa-f]{2})+$'){throw 'Malformed HEX record or data after EOF'}
  $b=[Convert]::FromHexString($line.Substring(1));if($b.Length -ne $b[0]+5){throw 'HEX length mismatch'}
  $sum=0;foreach($byte in $b){$sum+=$byte};if(($sum-band 255)-ne 0){throw 'HEX checksum mismatch'}
  $offset=([int]$b[1]-shl 8)+[int]$b[2]
  switch($b[3]){
   0 {for($i=0;$i-lt $b[0];$i++){$addr=$base+$offset+$i;if($addr-lt 0xff0000 -or $addr-ge 0x1000000 -or $memory.ContainsKey($addr)){throw 'HEX overlap or out-of-profile code address'};$memory[$addr]=$b[4+$i]}}
   1 {if($b[0]-ne 0 -or $offset-ne 0){throw 'Invalid EOF'};$eof=$true}
   2 {if($b[0]-ne 2 -or $offset-ne 0){throw 'Invalid segment record'};$base=[long]((([int]$b[4]-shl 8)+[int]$b[5])-shl 4)}
   4 {if($b[0]-ne 2 -or $offset-ne 0){throw 'Invalid linear record'};$base=[long]((([int]$b[4]-shl 8)+[int]$b[5])-shl 16)}
   default {throw "Unsupported HEX record type: $($b[3])"}
  }
 }
 if(-not $eof -or -not $memory.ContainsKey(0xff0000L)){throw 'Missing EOF or reset vector'}
 $map=[IO.File]::ReadAllText((Join-Path $outputRoot 'linked.partial.map'))
 foreach($pair in @(@('s_SSEG',0x400),@('l_SSEG',0x400),@('__mcs251_stack_initial',0x3ff))){
  $match=[regex]::Match($map,'(?m)^[CD]:\s+([0-9A-F]+)\s+'+[regex]::Escape($pair[0])+'\s*$')
  if(-not $match.Success -or [Convert]::ToInt32($match.Groups[1].Value,16)-ne $pair[1]){throw "Link contract mismatch: $($pair[0])"}
 }
 [IO.File]::Move($hex,(Join-Path $outputRoot 'firmware.hex'))
 $receipt=[ordered]@{status='complete';schema=2;target=$config.target;model=$config.model;optimization=$config.optimization;runtime=$runtime;projectSnapshotSha256=(Hash-File (Join-Path $outputRoot 'project.snapshot.json'));profileLockSha256=(Hash-File $lockPath);compilerSha256=(Hash-File (Join-Path $toolRoot 'bin/sdcc.exe'));firmwareSha256=(Hash-File (Join-Path $outputRoot 'firmware.hex'));codeBytes=$memory.Count;stackBase=1024;stackBytes=1024;inputs=$inputs;hardwareExecuted=$false}
 Write-Json (Join-Path $outputRoot 'receipt.json') $receipt
 Write-Json (Join-Path $outputRoot 'build-state.json') @{status='complete';firmware='firmware.hex';firmwareSha256=$receipt.firmwareSha256}
 Write-Output "Build complete: $outputRoot\firmware.hex ($($memory.Count) code bytes)"
 exit 0
} catch {
 $message=$_.Exception.Message
 if($ownsOutput){
  foreach($name in @('firmware.hex','linked.partial.hex','receipt.json')){$path=Join-Path $outputRoot $name;if([IO.File]::Exists($path)){[IO.File]::Delete($path)}}
  Write-Json (Join-Path $outputRoot 'build-state.json') @{status='failed';error=$message}
 }
 [Console]::Error.WriteLine($message)
 exit 1
}
