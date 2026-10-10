param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$BuildArtifactsDirectory,
    [string]$ValidationInputs
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new Keil workflow output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$sdk = Join-Path $build 'bin/StudioX.Extensions.Abstractions/release_win-x64/StudioX.Extensions.Abstractions.dll'
$cli = Join-Path $build 'bin/StudioX.Cli/release_win-x64/StudioX.Cli.exe'
$desktop = Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
foreach ($required in @($sdk, $cli, $desktop)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Build the solution first: $required" }
}
function Invoke-Checked([scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Keil workflow check failed with exit code $LASTEXITCODE." }
}
Invoke-Checked { & dotnet build (Join-Path $root 'plugins/StudioX.KeilImporter/Validation/StudioX.KeilImporter.Validation.csproj') -c Release --artifacts-path $build --nologo "-p:StudioXSdkPath=$sdk" }
Invoke-Checked { & dotnet build (Join-Path $root 'tools/StudioX.PluginRuntimeValidation/StudioX.PluginRuntimeValidation.csproj') -c Release --artifacts-path $build --nologo }
$validator = Join-Path $build 'bin/StudioX.KeilImporter.Validation/release_win-x64/StudioX.KeilImporter.Validation.exe'
Invoke-Checked { & $validator --offline (Join-Path $output 'offline') }

function New-PluginArchive([string]$Name, [string]$Source, [object]$Manifest) {
    $staging = Join-Path $output ($Name + '-files')
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    foreach ($suffix in @('.dll', '.deps.json', '.runtimeconfig.json')) {
        $fileName = $Manifest.entryAssembly.Replace('.dll', $suffix)
        Copy-Item -LiteralPath (Join-Path $Source $fileName) -Destination (Join-Path $staging $fileName)
    }
    if ($Manifest.id -eq 'studiox.keil-importer') {
        Copy-Item -LiteralPath (Join-Path $root 'plugins/StudioX.KeilImporter/README.md') -Destination (Join-Path $staging 'README.md')
    }
    [IO.File]::WriteAllText((Join-Path $staging 'plugin.json'), ($Manifest | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    $archive = Join-Path $output ($Name + '.studioxplugin')
    Invoke-Checked { & $cli plugin pack $staging $archive } | Out-Host
    return $archive
}
$keilManifest = Get-Content -LiteralPath (Join-Path $root 'plugins/StudioX.KeilImporter/plugin.template.json') -Raw | ConvertFrom-Json
$keil = New-PluginArchive ('studiox.keil-importer-' + $keilManifest.version) (Join-Path $build 'bin/StudioX.KeilImporter/release_win-x64') $keilManifest
# 说明随可安装插件交付；打包后不再修改归档内容。
$fixtureManifest = [ordered]@{
    formatVersion = 1; apiVersion = 2; id = 'validation.application'; version = '1.0.0'
    scope = 'application'; displayName = '应用级会话验收'; kind = 'dotnet'
    entryAssembly = 'StudioX.PluginRuntimeValidation.dll'; entryType = 'StudioX.PluginRuntimeValidation.ApplicationValidationPlugin'
    capabilities = @('commands', 'panels'); hostTools = @(); sha256 = @{}
    activity = @{ version = 1; title = '应用会话验收'; tooltip = '应用级插件生命周期验收'; icon = @{ strokes = ,@(3,3,20,3,20,20,3,20,3,3) } }
}
$fixture = New-PluginArchive 'application-fixture' (Join-Path $build 'bin/StudioX.PluginRuntimeValidation/release_win-x64') $fixtureManifest
$ui = Join-Path $output 'ui'
$arguments = @('--preview-application-plugins', ('"' + $ui + '"'), ('"' + $fixture + '"'), ('"' + $keil + '"'))
$process = Start-Process -FilePath $desktop -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(120000)) {
    $process.Kill($true)
    throw 'Application plugin workflow UI timed out.'
}
$uiResultPath = Join-Path $ui 'result.json'
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $uiResultPath)) { throw 'Application plugin workflow UI failed.' }
$uiResult = Get-Content -LiteralPath $uiResultPath -Raw | ConvertFrom-Json
if ($uiResult.status -ne 'passed') { throw 'Application plugin UI did not report passed.' }
$realCompilation = 'not_requested'
if ($ValidationInputs) {
    $plan = Get-Content -LiteralPath ([IO.Path]::GetFullPath($ValidationInputs)) -Raw | ConvertFrom-Json
    foreach ($field in @('cli', 'packs', 'toolsets', 'f407Project', 'f103Project')) {
        if (!$plan.$field -or !(Test-Path -LiteralPath $plan.$field)) { throw "Missing explicit Keil validation input: $field" }
    }
    Invoke-Checked { & $validator $plan.cli $plan.packs $keil (Join-Path $output 'real-compilation') $plan.toolsets $plan.f407Project $plan.f103Project }
    Invoke-Checked { & dotnet build (Join-Path $root 'plugins/StudioX.KeilImporter/Validation/Renderer/RendererCheck.csproj') -c Release --artifacts-path $build --nologo "-p:StudioXSdkPath=$sdk" }
    $renderer = Join-Path $build 'bin/RendererCheck/release_win-x64/RendererCheck.exe'
    $desktopAssembly = [IO.Path]::ChangeExtension($desktop, '.dll')
    Invoke-Checked { & $renderer $desktopAssembly (Join-Path $output 'real-renderer') $plan.cli $plan.packs $plan.f407Project }
    $rendererResult = Get-Content -LiteralPath (Join-Path $output 'real-renderer/flow.json') -Raw | ConvertFrom-Json
    if (!$rendererResult.success) { throw 'Keil migration renderer did not report success.' }
    $realCompilation = 'passed'
}
[IO.File]::WriteAllText((Join-Path $output 'result.json'), (@{
    success = $true; applicationUiChecks = $uiResult.checks.Count; realCompilation = $realCompilation
    pluginArchive = $keil; pluginSha256 = (Get-FileHash -LiteralPath $keil -Algorithm SHA256).Hash
} | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$global:LASTEXITCODE = 0
Write-Output "Keil migration workflow passed: $output"
