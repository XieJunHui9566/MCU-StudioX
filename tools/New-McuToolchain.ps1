param([Parameter(Mandatory)][string]$ToolsetDirectory, [Parameter(Mandatory)][string]$OutputFile, [string]$SevenZipPath)
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetExtension($OutputFile) -ine '.mcutoolchain') { throw 'Development component packages use .mcutoolchain.' }
# 同一工具集清单直接进入组件包；不重编号、不重写清单，不改变已有工程锁定的指纹。
& (Join-Path $PSScriptRoot 'New-ToolsetArchive.ps1') -ToolsetDirectory $ToolsetDirectory -OutputFile $OutputFile -SevenZipPath $SevenZipPath
