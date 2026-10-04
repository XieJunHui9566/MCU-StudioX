function Resolve-StudioXDistributionProfile([string]$Profile)
{
    if ($Profile -eq 'base')
    {
        return 'light'
    }
    if ($Profile -notin @('full', 'light'))
    {
        throw 'Supported distribution profiles: full, light (base is a light alias).'
    }
    return $Profile
}

function Get-StudioXInstallerName([string]$Version, [string]$Profile)
{
    $profileName = Resolve-StudioXDistributionProfile $Profile
    if ($profileName -eq 'light')
    {
        return "MCU-StudioX-$Version-win-x64-Light-Setup.exe"
    }
    return "MCU-StudioX-$Version-win-x64-Setup.exe"
}

function Get-StudioXBundledComponents([string]$PayloadDirectory)
{
    $toolRoot = Join-Path ([IO.Path]::GetFullPath($PayloadDirectory)) 'runtime/toolsets'
    if (!(Test-Path -LiteralPath $toolRoot))
    {
        return
    }
    Assert-StudioXReleaseDirectory $toolRoot
    foreach ($id in Get-ChildItem -LiteralPath $toolRoot -Force)
    {
        if (!$id.PSIsContainer -or $id.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')
        {
            throw "Invalid bundled component directory: $($id.Name)"
        }
        Assert-StudioXReleaseDirectory $id.FullName
        foreach ($version in Get-ChildItem -LiteralPath $id.FullName -Force)
        {
            if (!$version.PSIsContainer -or $version.Name -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')
            {
                throw 'Invalid bundled component version directory.'
            }
            Assert-StudioXReleaseDirectory $version.FullName
            $manifestPath = Join-Path $version.FullName 'toolset.json'
            $file = Get-Item -LiteralPath $manifestPath -Force
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt 32MB)
            {
                throw 'Linked or oversized component manifest.'
            }
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.formatVersion -ne 1 -or $manifest.id -cne $id.Name -or $manifest.version -cne $version.Name -or
                $manifest.host -ne 'win-x64' -or [string]::IsNullOrWhiteSpace($manifest.compilerId))
            {
                throw 'Bundled component identity does not match its directory.'
            }
            [ordered]@{id   =$manifest.id;
                version     =$manifest.version;
                host        =$manifest.host;
                compilerId  =$manifest.compilerId;
                fingerprint =(Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant();
                directory   =("runtime/toolsets/" + $id.Name + "/" + $version.Name)
            }
        }
    }
}

function Assert-StudioXReleaseDirectory([string]$Directory)
{
    $item = Get-Item -LiteralPath $Directory -Force
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))
    {
        throw "Linked or invalid release directory: $Directory"
    }
}

function Write-StudioXInstallerComponents([string]$PayloadDirectory, [string]$OutputFile)
{
    $components = @(Get-StudioXBundledComponents $PayloadDirectory)
    $directories = @($components | ForEach-Object { $_.directory.Replace('/', '\') })
    foreach ($auxiliary in @('runtime\hdl', 'runtime\stc-isp'))
    {
        if (Test-Path -LiteralPath (Join-Path $PayloadDirectory $auxiliary))
        {
            Assert-StudioXReleaseDirectory (Join-Path $PayloadDirectory $auxiliary)
            $directories += $auxiliary
        }
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('[Files]')
    foreach ($relative in $directories)
    {
        # 组件是整目录保留，不能按单个文件 onlyifdoesntexist 拼出混合工具链。
        $lines.Add('Source: "{#PayloadDirectory}\' + $relative + '\*"; DestDir: "{app}\' + $relative + '"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall; Check: ShouldInstallDevelopmentDirectory(''' + $relative + ''')')
    }
    $lines.Add('[Code]')
    $lines.Add('procedure SnapshotDevelopmentDirectories;')
    $lines.Add('begin')
    foreach ($relative in $directories)
    {
        $lines.Add('  SnapshotDevelopmentDirectory(''' + $relative + ''');')
    }
    $lines.Add('end;')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputFile))) | Out-Null
    [IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutputFile), $lines, [Text.UTF8Encoding]::new($false))
}
