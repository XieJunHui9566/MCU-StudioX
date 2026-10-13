function Read-StudioXMaintenancePolicy
{
    $policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'maintenance-gates.json') -Raw | ConvertFrom-Json
    if ($policy.formatVersion -ne 1) { throw 'Unsupported maintenance policy.' }
    return $policy
}

function Get-StudioXMaintenanceAreas([object]$Policy, [string[]]$Areas)
{
    $normalized = @('baseline') + @($Areas) | Select-Object -Unique
    foreach ($area in $normalized)
    {
        if ($area -cnotin @($Policy.profiles.id)) { throw "Unknown maintenance area: $area" }
    }
    return @($normalized)
}

function Assert-StudioXMaintenanceInputs([object]$Policy, [string[]]$Areas, [Collections.IDictionary]$Inputs)
{
    foreach ($profile in $Policy.profiles | Where-Object { $_.id -cin $Areas })
    {
        foreach ($name in $profile.requiredInputs)
        {
            $value = $Inputs[$name]
            if ([string]::IsNullOrWhiteSpace($value)) { throw "Maintenance '$($profile.id)' requires -$name; missing input is not a pass." }
            $kind = if ($Policy.inputs.$name -eq 'directory') { 'Container' } else { 'Leaf' }
            if (!(Test-Path -LiteralPath $value -PathType $kind)) { throw "Maintenance input does not exist: -$name = $value" }
        }
    }
}

function Get-StudioXMaintenanceSourceSnapshot([string]$SourceDirectory)
{
    # 未提交源码也必须绑定内容；HEAD 和 dirty 布尔值不能区分同一提交下的两次修改。
    $paths = @(& git -c core.quotepath=false -C $SourceDirectory ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate maintenance source.' }
    $paths = @($paths | Select-Object -Unique)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $lines = [Text.StringBuilder]::new()
    foreach ($relative in $paths)
    {
        $file = Join-Path $SourceDirectory $relative
        if (Test-Path -LiteralPath $file -PathType Leaf)
        {
            if (([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Source link cannot be fingerprinted: $relative" }
            $digest = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        }
        else { $digest = 'missing' }
        [void]$lines.Append($relative).Append([char]0).Append($digest).AppendLine()
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($lines.ToString())
    return [pscustomobject]@{algorithm='sha256-git-visible-files-v1'; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); fileCount=$paths.Count}
}

function Get-StudioXMaintenanceInputFiles([object]$Policy, [Collections.IDictionary]$Inputs)
{
    foreach ($name in $Inputs.Keys | Sort-Object)
    {
        if ($Policy.inputs.$name -eq 'file')
        {
            $path = [IO.Path]::GetFullPath($Inputs[$name])
            [pscustomobject]@{name=$name; path=$path; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash}
        }
    }
}

function Resolve-StudioXMaintenanceEvidencePath([string]$Directory, [string]$Relative)
{
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) { throw 'Evidence path must be relative.' }
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Evidence path escapes its directory: $Relative" }
    # 证据路径不得通过链接指向外部文件。
    $boundary = [IO.Path]::GetFullPath($Directory)
    for ($item = $path; $item.Length -ge $boundary.TrimEnd('\', '/').Length; $item = [IO.Path]::GetDirectoryName($item))
    {
        if ((Test-Path -LiteralPath $item) -and ([IO.File]::GetAttributes($item) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Evidence link is not supported: $Relative" }
    }
    return $path
}

function Test-StudioXMaintenanceEvidence([object]$Policy, [string[]]$Areas, [object]$Report, [string]$EvidenceDirectory, [object]$CurrentSnapshot, [string]$CurrentCommit)
{
    $errors = [Collections.Generic.List[string]]::new()
    $required = @($Policy.baselineGates) + @($Policy.profiles | Where-Object { $_.id -cin $Areas } | ForEach-Object { $_.gates }) | Select-Object -Unique
    if ($Report.formatVersion -ne 1 -or $Report.passed -isnot [bool] -or !$Report.passed) { $errors.Add('Regression did not report a successful supported run.') }
    if ($Report.hardware -isnot [bool] -or $Report.hardware -or $Report.downloadedSdk -isnot [bool] -or $Report.downloadedSdk) { $errors.Add('Software maintenance must not imply hardware or SDK-download validation.') }
    if ($Report.sourceCommit -cne $CurrentCommit) { $errors.Add('Source commit differs from the regression run.') }
    if ($Report.sourceSnapshot.before.algorithm -cne 'sha256-git-visible-files-v1' -or
        $Report.sourceSnapshot.after.algorithm -cne 'sha256-git-visible-files-v1' -or
        $Report.sourceSnapshot.before.sha256 -cne $Report.sourceSnapshot.after.sha256 -or
        $Report.sourceSnapshot.before.sha256 -cne $CurrentSnapshot.sha256)
    {
        $errors.Add('Source snapshot is absent or stale; earlier evidence is historical only.')
    }
    foreach ($area in $Areas)
    {
        if ($area -cnotin @($Report.maintenanceAreas)) { $errors.Add("Area was not declared for this run: $area") }
    }
    $inputs = @{}
    if ($Report.maintenanceInputs -is [Collections.IDictionary]) { $inputs = $Report.maintenanceInputs }
    else { foreach ($property in $Report.maintenanceInputs.PSObject.Properties) { $inputs[$property.Name] = $property.Value } }
    foreach ($profile in $Policy.profiles | Where-Object { $_.id -cin $Areas })
    {
        foreach ($name in $profile.requiredInputs)
        {
            # 目录身份由专项报告核对；文件型输入在下面另核对内容，缺输入不能冒充完整运行。
            if ([string]::IsNullOrWhiteSpace($inputs[$name])) { $errors.Add("Run omitted required input: $name") }
            if ($Policy.inputs.$name -eq 'file' -and @($Report.inputFileSnapshot.before | Where-Object { $_.name -ceq $name }).Count -ne 1)
            {
                $errors.Add("Required input has no unique content snapshot: $name")
            }
        }
    }
    foreach ($file in $Report.inputFileSnapshot.before)
    {
        try
        {
            $after = @($Report.inputFileSnapshot.after | Where-Object { $_.name -ceq $file.name })
            if ($after.Count -ne 1 -or $file.path -cne $inputs[$file.name] -or $file.path -cne $after[0].path -or
                $file.sha256 -cne $after[0].sha256 -or $file.sha256 -cne (Get-FileHash -LiteralPath $file.path -Algorithm SHA256 -ErrorAction Stop).Hash)
            {
                $errors.Add("Input file is absent or stale: $($file.name)")
            }
        }
        catch { $errors.Add("$($file.name): " + $_.Exception.ToString()) }
    }
    foreach ($group in @($Report.results | Group-Object -Property name))
    {
        if ($group.Count -ne 1) { $errors.Add("Duplicate gate record: $($group.Name)") }
    }
    foreach ($result in $Report.results)
    {
        if ($result.passed -isnot [bool] -or !$result.passed) { $errors.Add("Failed gate: $($result.name)") }
        try
        {
            $log = Resolve-StudioXMaintenanceEvidencePath $EvidenceDirectory $result.log
            if (!(Test-Path -LiteralPath $log -PathType Leaf)) { $errors.Add("Missing original log: $($result.name)") }
        }
        catch { $errors.Add($_.Exception.Message) }
    }
    foreach ($name in $required)
    {
        if (@($Report.results | Where-Object { $_.name -ceq $name }).Count -ne 1) { $errors.Add("Required gate missing: $name") }
    }
    foreach ($profile in $Policy.profiles | Where-Object { $_.id -cin $Areas })
    {
        foreach ($summary in $profile.reports)
        {
            try
            {
                $path = Resolve-StudioXMaintenanceEvidencePath $EvidenceDirectory $summary.path
                $data = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                foreach ($property in $summary.expected.PSObject.Properties)
                {
                    $actual = $data.($property.Name)
                    if ($actual -isnot $property.Value.GetType() -or $actual -cne $property.Value) { $errors.Add("$($summary.path): $($property.Name) must equal $($property.Value).") }
                }
            }
            catch { $errors.Add("$($summary.path): " + $_.Exception.ToString()) }
        }
    }
    return [pscustomobject]@{formatVersion=1; passed=($errors.Count -eq 0); areas=@($Areas); requiredGates=@($required); errors=@($errors); sourceSha256=$CurrentSnapshot.sha256}
}
