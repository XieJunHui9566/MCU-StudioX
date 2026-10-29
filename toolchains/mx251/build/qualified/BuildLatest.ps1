#requires -Version 7.0
param(
 [Parameter(Mandatory=$true)][string]$BuildScript,
 [Parameter(Mandatory=$true)][string]$Project,
 [Parameter(Mandatory=$true)][string]$ToolchainRoot,
 [Parameter(Mandatory=$true)][string]$ProfileRoot,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [Parameter(Mandatory=$true)][string]$CMakeInput,
 [switch]$LeafCallFeedback
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$lease=$null;$ownsOutput=$false;$outputRoot=$null
function Save-Json([string]$Path,$Value) {
 [IO.File]::WriteAllText($Path,(ConvertTo-Json -InputObject $Value -Depth 20),[Text.UTF8Encoding]::new($false))
}
function Check-Ancestors([string]$Path) {
 $probe=$Path
 while($probe) {
  if(Test-Path -LiteralPath $probe) {
   if((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Output path traverses a reparse point: $probe"}
  }
  $probe=Split-Path -Parent $probe
 }
}
try {
 $projectPath=[IO.Path]::GetFullPath($Project)
 $projectRoot=Split-Path -Parent $projectPath
 $outputRoot=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
 $expected=[IO.Path]::GetFullPath((Join-Path $projectRoot '.build/mx251/latest'))
 if(-not $outputRoot.Equals($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Managed output must be this project\ .build\mx251\latest'}
 Check-Ancestors $outputRoot
 $parent=Split-Path -Parent $outputRoot
 [IO.Directory]::CreateDirectory($parent)|Out-Null
 # 锁文件保留；删除锁文件会使另一个进程锁住不同文件，破坏单构建所有者约定。
 try {
  $lockPath=Join-Path $parent 'build.lock'
  if($env:MX251_OUTPUT_LEASE) {
   # IDE 可保留读租约直到发布；独立 CLI 不需要 IDE 或该环境变量。
   $lease=[IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
   $reader=[IO.StreamReader]::new($lease,[Text.Encoding]::UTF8,$true,1024,$true)
   try {if($reader.ReadToEnd() -cne $env:MX251_OUTPUT_LEASE){throw 'Output lease mismatch'}} finally {$reader.Dispose()}
  } else {$lease=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
 }
 catch {throw 'Another build owns this output; no files were changed.'}
 if(Test-Path -LiteralPath $outputRoot) {
  if(-not (Test-Path -LiteralPath (Join-Path $outputRoot 'owner.json') -PathType Leaf)){throw 'Existing output has no ownership record; preserve it and choose an empty qualified project.'}
  $owner=Get-Content -LiteralPath (Join-Path $outputRoot 'owner.json') -Raw | ConvertFrom-Json
  if($owner.schema -ne 1 -or $owner.project -cne $projectPath){throw 'Output ownership does not match this project; existing files preserved.'}
  # 在同一 PowerShell 进程内核对绝对路径和目录链接后才递归清理。
  Check-Ancestors $outputRoot
  if(@(Get-ChildItem -LiteralPath $outputRoot -Force -Recurse | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Owned output contains a reparse point; cleanup refused.'}
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
 }
 $ownsOutput=$true
 # 在启动编译器之前记录所有权：整个进程树被取消后，下次仍可识别并清理半成品。
 [IO.Directory]::CreateDirectory($outputRoot)|Out-Null
 Save-Json (Join-Path $outputRoot 'owner.json') @{schema=1;project=$projectPath}
 # 每次从干净目录全量构建，优先保证头文件/工具/运行库变动必然生效；尚非增量编译。
 $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=[Environment]::ProcessPath
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$BuildScript,'-Project',$projectPath,'-ToolchainRoot',$ToolchainRoot,'-ProfileRoot',$ProfileRoot,'-OutputDirectory',$outputRoot,'-CMakeInput',$CMakeInput,'-ManagedOutput')){$start.ArgumentList.Add($argument)}
 if($LeafCallFeedback){$start.ArgumentList.Add('-LeafCallFeedback')}
 $process=[Diagnostics.Process]::new();$process.StartInfo=$start
 try {
  if(-not $process.Start()){throw 'Cannot start the qualified build script'}
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(600000)){$process.Kill($true);$process.WaitForExit();throw 'Managed build timeout'}
  $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult();$code=$process.ExitCode
 } finally {$process.Dispose()}
 [IO.Directory]::CreateDirectory($outputRoot)|Out-Null
 Save-Json (Join-Path $outputRoot 'owner.json') @{schema=1;project=$projectPath}
 [IO.File]::WriteAllText((Join-Path $outputRoot 'build.stdout.log'),$out)
 [IO.File]::WriteAllText((Join-Path $outputRoot 'build.stderr.log'),$err)
 [Console]::Out.Write($out);[Console]::Error.Write($err)
 if($code -ne 0){throw "Build failed with exit code $code; original diagnostics retained"}
 if(-not (Test-Path -LiteralPath (Join-Path $outputRoot 'receipt.json'))){throw 'Build returned success without a receipt'}
} catch {
 $message=$_.Exception.Message
 if($ownsOutput) {
  [IO.Directory]::CreateDirectory($outputRoot)|Out-Null
  foreach($name in @('firmware.hex','firmware.bin','linked.partial.hex','receipt.json')) {
   $p=Join-Path $outputRoot $name;if([IO.File]::Exists($p)){[IO.File]::Delete($p)}
  }
  Save-Json (Join-Path $outputRoot 'owner.json') @{schema=1;project=$projectPath}
  Save-Json (Join-Path $outputRoot 'build-state.json') @{status='failed';error=$message}
 }
 [Console]::Error.WriteLine($message);exit 1
} finally {if($null -ne $lease){$lease.Dispose()}}
