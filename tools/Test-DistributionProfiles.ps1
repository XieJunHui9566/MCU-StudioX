param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$DesktopExe,
    [string]$CompilerPath = (Join-Path $PSScriptRoot '../.artifacts/installer-tools/InnoSetup-7.1.0/ISCC.exe'),
    [ValidateRange(30, 900)][int]$ProcessTimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Distribution-Profile.ps1')
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root)
{
    throw 'Use a new profile validation directory.'
}
[IO.Directory]::CreateDirectory($root) | Out-Null
$sourceRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw)).SelectSingleNode('//ProductVersion').InnerText
$identity = Get-StudioXReleaseVersion $version
$checks = [Collections.Generic.List[string]]::new()
$productionRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B8050FBC-2DE2-43F4-B839-F0E90522E671}_is1'
$beforeRegistration = if (Test-Path -LiteralPath $productionRegistry)
{
    Get-ItemProperty -LiteralPath $productionRegistry | Select-Object DisplayVersion, InstallLocation | ConvertTo-Json
}
else
{
    ''
}
function Check([bool]$Condition, [string]$Label)
{
    if (!$Condition)
    {
        throw $Label
    };
    $checks.Add($Label);
    Write-Output "PASS $Label"
}
function Run([string]$Program, [string[]]$Arguments, [string]$Log)
{
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Program))
    $start.UseShellExecute = $false;
    $start.CreateNoWindow = $true;
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true;
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync();
    $stderr = $process.StandardError.ReadToEndAsync()
    # 安装器资源更新在部分 Windows 主机上较慢，仍保留有界超时与进程树终止。
    $deadline = [DateTime]::UtcNow.AddSeconds($ProcessTimeoutSeconds)
    while (!$process.WaitForExit(1000))
    {
        if ([DateTime]::UtcNow -gt $deadline)
        {
            $process.Kill($true);
            throw 'Fixture process timed out.'
        }
    }
    [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    return $process.ExitCode
}
function WriteFile([string]$Relative, [string]$Text, [string]$Directory)
{
    $path = Join-Path $Directory $Relative;
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}
function Tree([string]$Directory)
{
    return @((Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
                [IO.Path]::GetRelativePath($Directory, $_.FullName) + ' ' + (Get-FileHash -LiteralPath $_.FullName).Hash
            }))
}
function Fixture([string]$Directory, [bool]$Full)
{
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    Copy-Item -LiteralPath $DesktopExe -Destination (Join-Path $Directory 'MCU StudioX.exe')
    WriteFile 'device-packs/index.json' '[]' $Directory
    WriteFile 'runtime/plugins/test/plugin.json' '{"fixture":true}' $Directory
    WriteFile 'runtime/languages/clangd/bin/clangd.exe' 'language-service-fixture' $Directory
    WriteFile 'runtime/git/cmd/git.exe' 'git-fixture' $Directory
    WriteFile 'runtime/git/mingw64/libexec/git-core/dlls-copied.exe' 'git-support-fixture' $Directory
    WriteFile '使用说明.txt' 'installation semantics fixture, no tools are executed' $Directory
    @{version               =$version;
        distributionProfile =$(if ($Full)
            {
                'full'
            }
            else
            {
                'light'
            })
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'release.json')
    if ($Full)
    {
        foreach ($id in @('agm.agrv', 'arm.gnu', 'riscv.xpack', 'wch.riscv'))
        {
            $component = "runtime/toolsets/$id/1.0.0"
            WriteFile "$component/probe.exe" 'not an executable, installation fixture only' $Directory
            @{formatVersion =1;
                id          =$id;
                version     ='1.0.0';
                host        ='win-x64';
                compilerId  ='fixture-gcc';
                executables =@{gcc = 'probe.exe' };
                sha256      =@{'probe.exe' = (Get-FileHash -LiteralPath (Join-Path $Directory "$component/probe.exe")).Hash }
            } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory "$component/toolset.json")
            WriteFile "$component/sdk/include/header.h" 'complete nested payload' $Directory
        }
        WriteFile 'runtime/hdl/yosys/yosys.exe' 'legacy preview tool fixture' $Directory
        WriteFile 'runtime/stc-isp/python.exe' 'legacy download tool fixture' $Directory
    }
}
function Compile([string]$Payload, [string]$Profile, [string]$AppId, [string]$Label)
{
    $output = Join-Path $root $Label
    $entries = Join-Path $root ($Label + '.iss')
    Write-StudioXInstallerComponents $Payload $entries
    $code = Run $CompilerPath @('--quiet', '--no-compression', "--define=AppVersion=$version", "--define=AppFileVersion=$($identity.FileVersion)",
        "--define=DistributionProfile=$Profile", "--define=PayloadDirectory=$Payload", "--define=DevelopmentComponentEntries=$entries",
        "--define=ProductId=$AppId", "--define=ProductMutex=StudioX.Profile.$($AppId.Trim('{}'))", "--define=InstallerMutex=StudioX.Profile.Setup.$($AppId.Trim('{}'))",
        "--output-dir=$output", (Join-Path $PSScriptRoot 'installer/StudioX.iss')) (Join-Path $root ($Label + '.compile.log'))
    if ($code -ne 0)
    {
        throw "Fixture compiler failed ($Label), see compile log."
    }
    return Join-Path $output (Get-StudioXInstallerName $version $Profile)
}
function Install([string]$Setup, [string]$Directory, [string]$Label)
{
    return Run $Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', "/DIR=$Directory", "/LOG=$(Join-Path $root ($Label+'.setup.log'))") (Join-Path $root ($Label + '.process.log'))
}
$installed = Join-Path $root 'Installed fixture'
$legacyInstalled = Join-Path $root 'Legacy fixture'
$freshLightInstalled = Join-Path $root 'Fresh light fixture'
try
{
    Check ((Resolve-StudioXDistributionProfile 'base') -eq 'light') 'base compatibility entry resolves to the light profile'
    $full = Join-Path $root 'full-payload';
    $light = Join-Path $root 'light-payload'
    Fixture $full $true;
    Fixture $light $false
    Check (@(Get-StudioXBundledComponents $full).Count -eq 4 -and @(Get-StudioXBundledComponents $light).Count -eq 0) 'full inventory reads exact original identities; light inventory is empty'
    $appId = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $fullSetup = Compile $full 'full' $appId 'full-installer';
    $lightSetup = Compile $light 'light' $appId 'light-installer'
    Check ((Install $fullSetup $installed 'fresh-full') -eq 0) 'fresh full fixture installs successfully'
    foreach ($component in Get-StudioXBundledComponents $full)
    {
        Check ((@(Tree (Join-Path $full $component.directory)) -join "\n") -eq (@(Tree (Join-Path $installed $component.directory)) -join "\n")) "fresh component $($component.id) includes every nested file"
    }
    # 缺失清单、损坏内容、较新版本均保留，不让安装器拼接同版本文件；这些夹具从不运行。
    WriteFile 'runtime/toolsets/arm.gnu/1.0.0/toolset.json' 'user modified component manifest' $installed
    Remove-Item -LiteralPath (Join-Path $installed 'runtime/toolsets/arm.gnu/1.0.0/sdk/include/header.h')
    WriteFile 'runtime/toolsets/arm.gnu/2.0.0/user.txt' 'later imported newer component' $installed
    WriteFile 'runtime/toolsets/user.other/3.0.0/user.txt' 'unrelated imported component' $installed
    WriteFile 'runtime/toolsets/.retired/sentinel.txt' 'recoverable versions remain' $installed
    WriteFile 'runtime/hdl/yosys/user.txt' 'later auxiliary customization' $installed
    $preserved = Tree (Join-Path $installed 'runtime')
    WriteFile '.studiox/toolchain.lock.json' '{"fingerprint":"preserve"}' (Join-Path $root 'User project')
    WriteFile 'sdkconfig' 'preserve settings' (Join-Path $root 'User project')
    $projectBefore = Tree (Join-Path $root 'User project')
    Check ((Install $lightSetup $installed 'full-to-light') -eq 0) 'full to light same-version switch succeeds'
    Check ((@(Tree (Join-Path $installed 'runtime')) -join "\n") -eq ($preserved -join "\n")) 'light overlay preserves all tool versions, auxiliary tools and recoverable directories'
    WriteFile 'MCU StudioX.exe' 'damaged IDE program file' $installed
    Check ((Install $fullSetup $installed 'light-to-full') -eq 0) 'light to full same-version switch succeeds'
    Check ((Get-FileHash -LiteralPath (Join-Path $installed 'MCU StudioX.exe')).Hash -eq (Get-FileHash -LiteralPath $DesktopExe).Hash) 'same-version overlay repairs IDE program files'
    Check ((@(Tree (Join-Path $installed 'runtime')) -join "\n") -eq ($preserved -join "\n")) 'full overlay skips whole existing component directories, including damaged or incomplete versions'
    $registry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($appId)_is1"
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version) 'both flavors retain one product registration and identical version'
    Check ((Run (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') (Join-Path $root 'uninstall.log')) -eq 0) 'fixture uninstall succeeds'
    Check ((Test-Path -LiteralPath (Join-Path $installed 'runtime/toolsets/arm.gnu/1.0.0/toolset.json')) -and
        (Test-Path -LiteralPath (Join-Path $installed 'runtime/toolsets/riscv.xpack/1.0.0/sdk/include/header.h')) -and
        !(Test-Path -LiteralPath (Join-Path $installed 'MCU StudioX.exe')) -and !(Test-Path -LiteralPath $registry)) 'uninstall removes IDE but retains preinstalled and subsequently imported development components'
    Check ((@(Tree (Join-Path $root 'User project')) -join "\n") -eq ($projectBefore -join "\n")) 'switches and uninstall preserve external project locks and sdkconfig'
    $freshId = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $freshLightSetup = Compile $light 'light' $freshId 'fresh-light-installer'
    $freshFullSetup = Compile $full 'full' $freshId 'fresh-full-installer'
    Check ((Install $freshLightSetup $freshLightInstalled 'fresh-light') -eq 0) 'fresh light fixture installs successfully'
    Check (!(Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/toolsets')) -and
        !(Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/hdl')) -and
        !(Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/stc-isp')) -and
        (Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/plugins/test/plugin.json')) -and
        (Test-Path -LiteralPath (Join-Path $freshLightInstalled 'device-packs/index.json'))) 'fresh light keeps packs and plugins while installing no development tools'
    WriteFile 'runtime/toolsets/arm.gnu/1.0.0/user.txt' 'component imported after light install' $freshLightInstalled
    $outside = Join-Path $root 'Outside component boundary'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    WriteFile 'sentinel.txt' 'outside must remain unchanged' $outside
    $outsideBefore = Tree $outside
    $junction = Join-Path $freshLightInstalled 'runtime/toolsets/riscv.xpack'
    New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
    Check ((Install $freshFullSetup $freshLightInstalled 'linked-path-blocked') -ne 0) 'full overlay rejects a junction in the component destination before copying files'
    Check ((@(Tree $outside) -join "\n") -eq ($outsideBefore -join "\n")) 'rejected destination link leaves outside files unchanged'
    if (![IO.Path]::GetFullPath($junction).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Junction target is outside test root.'
    }
    [IO.Directory]::Delete($junction)
    Check ((Install $freshFullSetup $freshLightInstalled 'fresh-light-to-full') -eq 0) 'fresh light to full installs absent component versions'
    Check ((Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/toolsets/riscv.xpack/1.0.0/sdk/include/header.h')) -and
        (Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/toolsets/arm.gnu/1.0.0/user.txt')) -and
        !(Test-Path -LiteralPath (Join-Path $freshLightInstalled 'runtime/toolsets/arm.gnu/1.0.0/probe.exe'))) 'full fills absent components while preserving the entire partially imported version'
    Check ((Run (Join-Path $freshLightInstalled 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') (Join-Path $root 'fresh-light-uninstall.log')) -eq 0) 'fresh flavor-switch fixture uninstalls'

    $badOutput = Join-Path $root 'invalid-light-installer'
    $badCode = Run $CompilerPath @('--quiet', '--no-compression', "--define=AppVersion=$version", "--define=AppFileVersion=$($identity.FileVersion)",
        '--define=DistributionProfile=light', "--define=PayloadDirectory=$full", "--output-dir=$badOutput",
        (Join-Path $PSScriptRoot 'installer/StudioX.iss')) (Join-Path $root 'invalid-light.compile.log')
    Check ($badCode -ne 0 -and !(Test-Path -LiteralPath (Join-Path $badOutput (Get-StudioXInstallerName $version 'light')))) 'compiler rejects a mislabeled light payload containing tools'
    $rejected = $false
    try
    {
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -PayloadDirectory $full -DistributionProfile light -OutputDirectory (Join-Path $root 'invalid-light-builder') -CompilerPath $CompilerPath
    }
    catch
    {
        $rejected = $_.Exception.Message -match 'distribution profile'
    }
    Check $rejected 'installer builder rejects profile and payload identity mismatch before packaging'

    # 旧日志曾拥有工具文件；真实旧安装升级后卸载也必须保留这些文件。
    $legacyId = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $legacyScript = Join-Path $root 'legacy-owned.iss';
    $legacyOutput = Join-Path $root 'legacy-installer'
    @"
[Setup]
AppId={$legacyId
AppName=StudioX Legacy Fixture
AppVersion=0.2.5.4A
VersionInfoVersion=$($identity.FileVersion)
DefaultDirName=$legacyInstalled
PrivilegesRequired=lowest
SetupArchitecture=x64
UninstallDisplayName=StudioX Legacy Fixture
DisableProgramGroupPage=yes
OutputBaseFilename=Legacy
OutputDir=$legacyOutput
Compression=none
[Files]
Source: "$full\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
"@ | Set-Content -LiteralPath $legacyScript
    Check ((Run $CompilerPath @('--quiet', $legacyScript) (Join-Path $root 'legacy.compile.log')) -eq 0) 'legacy ownership fixture compiles with ordinary uninstall-owned tool files'
    Check ((Install (Join-Path $legacyOutput 'Legacy.exe') $legacyInstalled 'legacy-install') -eq 0) 'legacy ownership fixture installs'
    $legacyToolBefore = Tree (Join-Path $legacyInstalled 'runtime/toolsets')
    $legacyUpgrade = Compile $light 'light' $legacyId 'legacy-light-installer'
    Check ((Install $legacyUpgrade $legacyInstalled 'legacy-to-light') -eq 0) 'legacy full installation upgrades to light without clearing tools'
    Check ((Run (Join-Path $legacyInstalled 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') (Join-Path $root 'legacy-uninstall.log')) -eq 0) 'upgraded legacy fixture uninstalls'
    Check ((@(Tree (Join-Path $legacyInstalled 'runtime/toolsets')) -join "\n") -eq ($legacyToolBefore -join "\n")) 'uninstall after legacy upgrade no longer applies old tool ownership records'
    $afterRegistration = if (Test-Path -LiteralPath $productionRegistry)
    {
        Get-ItemProperty -LiteralPath $productionRegistry | Select-Object DisplayVersion, InstallLocation | ConvertTo-Json
    }
    else
    {
        ''
    }
    Check ($beforeRegistration -eq $afterRegistration) 'production installation registration remains unchanged'
    @{success              =$true;
        hardware           =$false;
        realToolExecution  =$false;
        isolatedProductIds =@($appId, $legacyId, $freshId);
        checks             =$checks
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'result.json')
}
catch
{
    @{success      =$false;
        checks     =$checks;
        diagnostic =$_.Exception.ToString()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'result.json')
    throw
}
finally
{
    # 失败时仅卸载本测试目录中的临时产品，不清理正式安装或递归删除任何目录。
    foreach ($directory in @($installed, $legacyInstalled, $freshLightInstalled))
    {
        $uninstaller = Join-Path $directory 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller)
        {
            $null = Run $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') (Join-Path $root ([IO.Path]::GetFileName($directory) + '.cleanup.log'))
        }
    }
}
