param(
    [string]$ReleaseVersion,
    [string]$PayloadDirectory,
    [string]$OutputDirectory,
    [string]$CompilerPath = (Join-Path $PSScriptRoot '../.artifacts/installer-tools/InnoSetup-7.1.0/ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$ReleaseVersion) { $ReleaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ReleaseVersion must be x.y.z.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/releases/$ReleaseVersion" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.Name -ne 'installer-build.log' }).Count) { throw 'Use a new installer output directory. Only a failed compilation log may be reused.' }
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'First run tools/Prepare-InstallerCompiler.ps1.' }
$compilerVersion = & $CompilerPath --version
if ($LASTEXITCODE -ne 0 -or "$compilerVersion" -notmatch '7\.1\.0') { throw 'Use the pinned Inno Setup 7.1.0 compiler.' }
if (!$PayloadDirectory) {
    $PayloadDirectory = Join-Path $projectRoot ('.artifacts/installer-payload-' + $ReleaseVersion + '-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $PayloadDirectory -ReleaseVersion $ReleaseVersion
}
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$release = Get-Content -LiteralPath (Join-Path $payload 'release.json') -Raw | ConvertFrom-Json
$executable = Join-Path $payload 'MCU StudioX.exe'
if ($release.version -ne $ReleaseVersion -or (Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$ReleaseVersion.0") { throw 'Payload and installer versions do not match.' }
foreach ($id in @('agm.agrv','arm.gnu','riscv.xpack','wch.riscv')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload "runtime/toolsets/$id/1.0.0/toolset.json"))) { throw "Missing bundled toolset: $id" }
}
if (!(Test-Path -LiteralPath (Join-Path $payload 'runtime/languages/clangd/bin/clangd.exe'))) { throw 'Missing bundled clangd.' }
if (!(Test-Path -LiteralPath (Join-Path $payload 'runtime/git/cmd/git.exe')) -or !(Test-Path -LiteralPath (Join-Path $payload 'runtime/git/mingw64/libexec/git-core/dlls-copied.exe'))) { throw 'Missing or incomplete bundled Git.' }
# 发行记录可检查安装后的每个文件，不把本机账户/工程数据带入包。
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked file in release: $($file.FullName)" }
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
    if ($relative -eq 'release-files.sha256.json') { continue }
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'release-files.sha256.json') -Encoding utf8
[IO.Directory]::CreateDirectory($output) | Out-Null
$script = Join-Path $PSScriptRoot 'installer/StudioX.iss'
$log = Join-Path $output 'installer-build.log'
[IO.File]::WriteAllText($log, '')
& $CompilerPath --quiet-progress "--define=AppVersion=$ReleaseVersion" "--define=PayloadDirectory=$payload" "--output-dir=$output" $script 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; see installer-build.log.' }
Add-Content -LiteralPath $log -Value "Inno Setup 7.1.0 compilation succeeded for MCU StudioX $ReleaseVersion."
$setup = Join-Path $output "MCU-StudioX-$ReleaseVersion-win-x64-Setup.exe"
if (!(Test-Path -LiteralPath $setup)) { throw 'Compiler did not produce the installer.' }
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
# 独立放一份包文件，方便作者单独更新和发送器件支持包。
Copy-Item -LiteralPath (Join-Path $payload 'device-packs') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $payload '使用说明.txt') -Destination $output
@{ version=$ReleaseVersion; installer=[IO.Path]::GetFileName($setup); sha256=$setupHash; bytes=(Get-Item -LiteralPath $setup).Length; sourceFiles=$hashes.Count; signingStatus='unsigned'; upgrade='Run the newer installer at the existing location; downgrades blocked'; createdUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
Write-Output $setup
