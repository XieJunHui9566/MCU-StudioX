function Write-AcceptanceJson([string]$Path, $Value)
{
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))) | Out-Null
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path)
    {
        [IO.File]::Replace($temporary, $Path, [NullString]::Value)
    }
    else
    {
        [IO.File]::Move($temporary, $Path)
    }
}
function Read-AcceptanceJson([string]$Path)
{
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Test-AcceptanceCreatedProject([string]$Project, [string]$Report, [string]$Installed, [string]$Pack, [string]$Device, [string]$Template, [string]$Name, [string]$Output)
{
    $projectExists = Test-Path -LiteralPath $Project -PathType Container
    $reportExists = Test-Path -LiteralPath $Report -PathType Leaf
    if (!$projectExists -and !$reportExists)
    {
        return $false
    }
    if (!$projectExists -or !$reportExists)
    {
        throw 'An interrupted project lacks its creation evidence; preserve it.'
    }
    $manifestPath = Join-Path $Project '.studiox/project.json'
    foreach ($path in @($Project, (Join-Path $Project '.studiox'), $manifestPath, $Report))
    {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            throw 'Project resume refuses links.'
        }
    }
    $created = Read-AcceptanceJson $Report;
    $actual = Read-AcceptanceJson $manifestPath
    if (!$created.passed -or !$created.bindings.passed -or !$created.bindings.actualShippingAssemblies -or [IO.Path]::GetFullPath($created.bindings.installed).TrimEnd('\') -ne [IO.Path]::GetFullPath($Installed).TrimEnd('\') -or [IO.Path]::GetFullPath($created.requirements.projectDirectory).TrimEnd('\') -ne [IO.Path]::GetFullPath($Project).TrimEnd('\'))
    {
        throw 'Project creation evidence belongs to another installation or project.'
    }
    if ($actual.packId -cne $Pack -or $actual.deviceId -cne $Device -or $actual.templateId -cne $Template -or $actual.name -cne $Name)
    {
        throw 'Interrupted project identity changed.'
    }
    # 产品文件和探针采用不同的枚举大小写；只归一化已核对的 Kind，不修改磁盘内容。
    if ($actual.kind -ine $created.manifest.kind)
    {
        throw 'Interrupted project kind changed.'
    }
    $actual.kind = $created.manifest.kind
    if (($actual | ConvertTo-Json -Depth 30 -Compress) -cne ($created.manifest | ConvertTo-Json -Depth 30 -Compress))
    {
        throw 'Interrupted project manifest changed; preserve the original evidence.'
    }
    Write-AcceptanceJson $Output @{passed =$true;
        project                           =$Project;
        manifestSha256                    =(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;
        creationReportSha256              =(Get-FileHash -LiteralPath $Report -Algorithm SHA256).Hash;
        resumedUtc                        =[DateTime]::UtcNow.ToString('o');
        existingFilesPreserved            =$true
    }
    return $true
}
function Quote-NativeArgument([string]$Value)
{
    # Windows 原生 argv 需要按引号前和尾部的反斜杠数量转义；不能使用 JSON 字符串替代。
    $text = [Text.StringBuilder]::new();
    [void]$text.Append('"');
    $slashes = 0
    foreach ($character in $Value.ToCharArray())
    {
        if ($character -eq '\')
        {
            $slashes++;
            continue
        }
        if ($character -eq '"')
        {
            [void]$text.Append([char]'\', 2 * $slashes + 1);
            [void]$text.Append('"')
        }
        else
        {
            [void]$text.Append([char]'\', $slashes);
            [void]$text.Append($character)
        }
        $slashes = 0
    }
    [void]$text.Append([char]'\', 2 * $slashes);
    [void]$text.Append('"');
    $text.ToString()
}
function Invoke-AcceptanceProcess([string]$File, [string[]]$Arguments, [string]$LogPrefix, [int]$TimeoutSeconds = 3600, [scriptblock]$Heartbeat)
{
    if (Test-Path -LiteralPath ($LogPrefix + '.process.json'))
    {
        $LogPrefix += '-retry-' + [Guid]::NewGuid().ToString('N')
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($LogPrefix))) | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false;
    $start.CreateNoWindow = $true
    $start.Arguments = (@($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' ')
    $start.RedirectStandardOutput = $true;
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8;
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync();
    $stderr = $process.StandardError.ReadToEndAsync()
    $started = [DateTime]::UtcNow;
    $deadline = $started.AddSeconds($TimeoutSeconds);
    $nextPulse = $started
    Write-AcceptanceJson ($LogPrefix + '.process.json') @{file =$File;
        arguments                                              =$Arguments;
        pid                                                    =$process.Id;
        startedUtc                                             =$started.ToString('o');
        complete                                               =$false
    }
    try
    {
        while (!$process.WaitForExit(1000))
        {
            if ([DateTime]::UtcNow -gt $deadline)
            {
                # 仅终止本次启动且仍由同一 Process 对象持有的进程树，保留安装器和编译器原始输出。
                & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F | Out-Null
                $process.WaitForExit(10000) | Out-Null
                throw ('Acceptance process timed out: ' + $File)
            }
            if ($Heartbeat -and [DateTime]::UtcNow -ge $nextPulse)
            {
                & $Heartbeat;
                $nextPulse = [DateTime]::UtcNow.AddSeconds(10)
            }
        }
        $process.WaitForExit()
        $code = $process.ExitCode
        Write-AcceptanceJson ($LogPrefix + '.process.json') @{file =$File;
            arguments                                              =$Arguments;
            pid                                                    =$process.Id;
            startedUtc                                             =$started.ToString('o');
            completedUtc                                           =[DateTime]::UtcNow.ToString('o');
            complete                                               =$true;
            exitCode                                               =$code
        }
        return $code
    }
    finally
    {
        if ($process.HasExited)
        {
            [IO.File]::WriteAllText($LogPrefix + '.stdout.log', $stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText($LogPrefix + '.stderr.log', $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        }
        $process.Dispose()
    }
}
function Get-AcceptanceEnvironment
{
    $result = @{}
    foreach ($scope in @('User', 'Machine'))
    {
        $values = @{}
        foreach ($name in @('PATH', 'IDF_PATH', 'IDF_TOOLS_PATH', 'IDF_PYTHON_ENV_PATH', 'PYTHONHOME', 'PYTHONPATH', 'DOTNET_ROOT'))
        {
            $values[$name] = [Environment]::GetEnvironmentVariable($name, $scope)
        }
        $result[$scope] = $values
    }
    return $result
}
function Assert-AcceptanceEnvironment($Before, $After)
{
    foreach ($scope in @('User', 'Machine'))
    {
        foreach ($property in $Before.$scope.PSObject.Properties)
        {
            if ($property.Value -ne $After[$scope][$property.Name])
            {
                throw ('Persistent environment changed: ' + $scope + '/' + $property.Name)
            }
        }
    }
}
