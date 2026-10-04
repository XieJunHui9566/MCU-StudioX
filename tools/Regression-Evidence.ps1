function Invoke-StudioXRegressionCheck([string]$CheckName, [scriptblock]$Action, [string]$EvidenceDirectory, [Collections.Generic.List[object]]$Results)
{
    $checkLog = Join-Path $EvidenceDirectory ($CheckName + '.log')
    $checkWatch = [Diagnostics.Stopwatch]::StartNew()
    try
    {
        # 原生命令更新全局退出码；局部同名变量会遮蔽失败结果，产生错误的通过记录。
        $global:LASTEXITCODE = 0
        . $Action *> $checkLog
        if ($global:LASTEXITCODE -ne 0)
        {
            throw "Command exited $global:LASTEXITCODE"
        }
        $Results.Add(@{name = $CheckName; passed = $true; elapsedSeconds = $checkWatch.Elapsed.TotalSeconds; log = [IO.Path]::GetRelativePath($EvidenceDirectory, $checkLog) })
        Write-Output "PASS $CheckName"
    }
    catch
    {
        $Results.Add(@{name = $CheckName; passed = $false; elapsedSeconds = $checkWatch.Elapsed.TotalSeconds; diagnostic = $_.Exception.ToString(); log = [IO.Path]::GetRelativePath($EvidenceDirectory, $checkLog) })
        throw "$CheckName failed; original diagnostics: $checkLog"
    }
}
