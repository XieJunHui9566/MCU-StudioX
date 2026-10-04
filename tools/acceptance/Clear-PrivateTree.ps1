function Clear-PrivateTree([string]$Target, [string]$Boundary, [string]$AuditDirectory)
{
    $scope = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    $destination = [IO.Path]::GetFullPath($Target).TrimEnd('\')
    $audit = [IO.Path]::GetFullPath($AuditDirectory).TrimEnd('\')
    if (!$destination.StartsWith($scope + '\', [StringComparison]::OrdinalIgnoreCase) -or ($audit -ne $scope -and !$audit.StartsWith($scope + '\', [StringComparison]::OrdinalIgnoreCase)) -or $audit.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or $audit -eq $destination)
    {
        throw 'Private cleanup escaped its selected scope.'
    }
    if (!(Test-Path -LiteralPath $destination -PathType Container))
    {
        return $null
    }
    foreach ($path in @($destination, $audit))
    {
        $cursor = $path
        while ($cursor -and $cursor.Length -ge $scope.Length)
        {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
            {
                throw 'Private cleanup refuses links.'
            }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
    }
    $items = @((Get-Item -LiteralPath $destination -Force)) + @(Get-ChildItem -LiteralPath $destination -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    {
        throw 'Private cleanup refuses linked tree entries.'
    }
    $knownBytes = 0L
    $sizeComplete = $true
    foreach ($item in $items | Where-Object { !$_.PSIsContainer })
    {
        # 5.1 的 FileInfo.Length 在部分长路径上无法读取；统计不能阻断已限定范围的安全清理。
        if ($item.FullName.Length -ge 248)
        {
            $sizeComplete = $false;
            continue
        }
        try
        {
            $knownBytes += [long]$item.Length
        }
        catch
        {
            $sizeComplete = $false
        }
    }
    $bytes = if ($sizeComplete)
    {
        $knownBytes
    }
    else
    {
        $null
    }
    $token = [Guid]::NewGuid().ToString('N')
    $empty = Join-Path $scope ('cleanup-empty-' + $token)
    $log = Join-Path $audit ('cleanup-robocopy-' + $token + '.log')
    $planPath = Join-Path $audit ('cleanup-tree-' + $token + '.json')
    $plan = @{formatVersion    =1;
        target                 =$destination;
        boundary               =$scope;
        startedUtc             =[DateTime]::UtcNow.ToString('o');
        bytes                  =$bytes;
        knownBytes             =$knownBytes;
        byteAccountingComplete =$sizeComplete;
        method                 ='Windows robocopy /E /PURGE /XJ from a new private empty directory';
        log                    =$log;
        complete               =$false
    }
    [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $empty)
    {
        throw 'Preserve an existing cleanup source.'
    }
    [IO.Directory]::CreateDirectory($empty) | Out-Null
    try
    {
        # PowerShell 5.1 的递归删除会在 SDK 长路径上失败；系统 robocopy 默认支持长路径，且不跟随链接。
        & (Join-Path $env:SystemRoot 'System32/robocopy.exe') $empty $destination /E /PURGE /XJ /NOCOPY /DCOPY:DA /R:0 /W:0 /NP /NFL /NDL /NJH /NJS ('/LOG:' + $log) | Out-Null
        $code = $LASTEXITCODE
        if ($code -ge 8)
        {
            throw ('Private long-path cleanup failed; robocopy exit ' + $code + '. Raw log: ' + $log)
        }
        if (@(Get-ChildItem -LiteralPath $destination -Force).Count)
        {
            throw 'Private cleanup left tree entries; preserve them.'
        }
        Remove-Item -LiteralPath $destination -Force
        $plan.complete = $true
        $plan.robocopyExitCode = $code
        $plan.completedUtc = [DateTime]::UtcNow.ToString('o')
        [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        return @{path              =$destination;
            bytes                  =$bytes;
            knownBytes             =$knownBytes;
            byteAccountingComplete =$sizeComplete;
            log                    =$log;
            plan                   =$planPath;
            robocopyExitCode       =$code
        }
    }
    finally
    {
        if ((Test-Path -LiteralPath $empty) -and !@(Get-ChildItem -LiteralPath $empty -Force).Count)
        {
            Remove-Item -LiteralPath $empty -Force
        }
    }
}
