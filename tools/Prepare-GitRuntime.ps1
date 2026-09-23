param([string]$SevenZip = 'C:\Program Files\7-Zip\7z.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = '2.55.0.5'
$sha256 = '5AA8A20F6E9ABB2C755F0E73C91C687701A46B309AD84A0CA6509380FA4AE290'
$url = "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/PortableGit-$version-64-bit.7z.exe"
$destination = Join-Path $projectRoot 'artifacts/git-runtime/git'
if (Test-Path -LiteralPath (Join-Path $destination 'studiox-provenance.json')) {
    $prepared = Get-Content -LiteralPath (Join-Path $destination 'studiox-provenance.json') -Raw | ConvertFrom-Json
    if ($prepared.preparationVersion -eq 1 -and $prepared.sha256 -eq $sha256) {
        if (!(Test-Path -LiteralPath (Join-Path $destination 'mingw64/bin/git-credential-manager.exe') -PathType Leaf)) { throw 'Prepared Git runtime is missing Git Credential Manager; inspect the staging directory.' }
        Write-Output $destination; return
    }
} elseif (Test-Path -LiteralPath $destination) { throw 'Inspect incomplete Git staging directory before retrying.' }
if (!(Test-Path -LiteralPath $SevenZip)) { throw 'Provide a 7-Zip executable with -SevenZip.' }
$downloads = Join-Path $projectRoot '.artifacts/downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$archive = Join-Path $downloads "PortableGit-$version-64-bit.7z.exe"
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) { throw 'Portable Git SHA-256 mismatch.' }
# 仅将官方便携归档当作压缩文件读取，不运行安装器，不改写系统 PATH。
if (!(Test-Path -LiteralPath $destination)) {
    & $SevenZip x $archive "-o$destination" -y -bso0
    if ($LASTEXITCODE -ne 0) { throw 'Portable Git extraction failed.' }
}
elseif (!(Test-Path -LiteralPath (Join-Path $destination 'post-install.bat'))) {
    & $SevenZip x $archive "-o$destination" 'post-install.bat' 'etc/post-install/*' -y -bso0
    if ($LASTEXITCODE -ne 0) { throw 'Cannot restore upstream post-install scripts.' }
}
if (!(Test-Path -LiteralPath (Join-Path $destination 'cmd/git.exe'))) { throw 'Portable Git executable missing.' }
if (!(Test-Path -LiteralPath (Join-Path $destination 'mingw64/bin/git-credential-manager.exe'))) { throw 'Portable Git Credential Manager executable missing.' }
# 官方 README.portable 要求解包后执行 post-install；它只初始化此便携目录。
# 其中会复制本机 hosts 等网络文件，必须在发行前移除，不能把开发机配置发给用户。
$taskGitRoot = [IO.Path]::GetFullPath($destination)
if (!$taskGitRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Git staging escaped workspace.' }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/cmd.exe'))
$start.WorkingDirectory = $taskGitRoot; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
# 厂商脚本内部调用 git --exec-path，不能误选开发机已有的 Git。
$start.Environment['PATH'] = (Join-Path $taskGitRoot 'cmd') + ';' + (Join-Path $taskGitRoot 'mingw64/bin') + ';' + (Join-Path $taskGitRoot 'usr/bin') + ';' + $env:PATH
foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'GIT_*' })) { $start.Environment.Remove($key) | Out-Null }
$start.ArgumentList.Add('/d'); $start.ArgumentList.Add('/c'); $start.ArgumentList.Add('post-install.bat')
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit(120000)) { $process.Kill($true); throw 'Portable Git post-install timed out.' }
$diagnostics = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
foreach ($name in @('hosts', 'protocols', 'services', 'networks')) {
    $taskLocalFile = [IO.Path]::GetFullPath((Join-Path $taskGitRoot "etc/$name"))
    if (!$taskLocalFile.StartsWith($taskGitRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected Git configuration path.' }
    if (Test-Path -LiteralPath $taskLocalFile) { Remove-Item -LiteralPath $taskLocalFile -Force }
}
if (!(Test-Path -LiteralPath (Join-Path $taskGitRoot 'mingw64/libexec/git-core/dlls-copied.exe'))) { throw "Portable Git helper DLL initialization missing. $diagnostics" }
# 官方清理脚本最后 test/rm 的退出码不能代表初始化结果，以上按实际文件验证。
@{ version=$version; preparationVersion=1; url=$url; sha256=$sha256; source='https://github.com/git-for-windows/git/tree/v2.55.0.windows.5'; upstream='https://gitforwindows.org/'; preparation='Official post-install; machine-specific etc/hosts, protocols, services and networks omitted.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'studiox-provenance.json') -Encoding utf8
Write-Output $destination
