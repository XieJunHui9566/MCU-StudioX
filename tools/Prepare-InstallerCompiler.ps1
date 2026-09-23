param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../.artifacts/installer-tools'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $root 'InnoSetup-7.1.0/ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
[IO.Directory]::CreateDirectory($root) | Out-Null
$download = Join-Path $root 'innosetup-7.1.0-x64.exe'
$uri = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
& curl.exe -fL --retry 3 --connect-timeout 20 --max-time 180 $uri --output $download
if ($LASTEXITCODE -ne 0) { throw 'Cannot download Inno Setup compiler.' }
$expected = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Inno Setup SHA-256 mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Inno Setup publisher verification failed.' }
$directory = Join-Path $root 'InnoSetup-7.1.0'
$process = Start-Process -FilePath $download -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /NOICONS /DIR=`"$directory`"" -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler installation failed.' }
@{ version='7.1.0'; url=$uri; sha256=$expected; publisher=$signature.SignerCertificate.Subject } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'compiler-provenance.json') -Encoding utf8
Write-Output $compiler
