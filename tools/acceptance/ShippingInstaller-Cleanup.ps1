function Complete-ShippingCleanup([string]$Root, [string]$Exchange)
{
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (![IO.Path]::GetFileName($rootPath).StartsWith('StudioX-Installer-', [StringComparison]::Ordinal))
    {
        throw 'Cleanup requires an explicitly named private installer fixture.'
    }
    $state = Read-AcceptanceJson (Join-Path $rootPath 'suite-state.json')
    if (!$state.passed -or !$state.complete)
    {
        throw 'Preserve fixtures until the shipping suite has passed.'
    }
    $export = Read-AcceptanceJson (Join-Path $rootPath 'export-status.json')
    $hostZip = Join-Path $Exchange 'installer-evidence.zip'
    if (!(Test-Path -LiteralPath $hostZip -PathType Leaf) -or (Get-Item -LiteralPath $hostZip).Length -ne $export.bytes -or (Get-FileHash -LiteralPath $hostZip -Algorithm SHA256).Hash -ne $export.sha256)
    {
        throw 'The preserved host export must match the completed guest export.'
    }
    $registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B8050FBC-2DE2-43F4-B839-F0E90522E671}_is1'
    if (Test-Path -LiteralPath $registry)
    {
        $installed = (Get-ItemProperty -LiteralPath $registry).'Inno Setup: App Path';
        if ([IO.Path]::GetFullPath($installed).StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase))
        {
            throw 'Uninstall the test application before cleanup.'
        }
    }
    $targets = @((Join-Path $rootPath 'work'), (Join-Path $rootPath 'evidence'))
    $sourceZip = Join-Path $rootPath 'installer-evidence.zip'
    if ((Test-Path -LiteralPath $sourceZip) -and (Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash -ne $export.sha256)
    {
        throw 'Guest source export changed; preserve all remaining fixtures.'
    }
    $receiptPath = Join-Path $rootPath 'cleanup.json'
    if (Test-Path -LiteralPath $receiptPath)
    {
        $receipt = Read-AcceptanceJson $receiptPath
        if (!$receipt.passed -or $receipt.exportSha256 -ne $export.sha256 -or @($targets | Where-Object { Test-Path -LiteralPath $_ }).Count -or (Test-Path -LiteralPath $sourceZip))
        {
            throw 'Completed cleanup receipt differs from remaining files.'
        }
        return $receipt
    }
    # 先保存恢复所需的证据身份；来宾 ZIP 或目录已删但回执未写时仍可安全重入。
    Write-AcceptanceJson (Join-Path $rootPath 'cleanup-intent.json') @{exportSha256 =$export.sha256;
        targets                                                                     =$targets;
        sourceZip                                                                   =$sourceZip;
        createdUtc                                                                  =[DateTime]::UtcNow.ToString('o')
    }
    $removed = @(foreach ($target in $targets)
        {
            if (Test-Path -LiteralPath $target)
            {
                Clear-PrivateTree $target $rootPath $rootPath
            }
        })
    if (Test-Path -LiteralPath $sourceZip)
    {
        Remove-Item -LiteralPath $sourceZip -Force
    }
    if (@($targets | Where-Object { Test-Path -LiteralPath $_ }).Count)
    {
        throw 'Installer fixture cleanup left intermediate directories.'
    }
    $receipt = @{formatVersion    =1;
        passed                    =$true;
        completedUtc              =[DateTime]::UtcNow.ToString('o');
        exportSha256              =$export.sha256;
        removed                   =$removed;
        intermediateTargetsAbsent =$targets;
        byteAccounting            ='Only this attempt; earlier interrupted removals are not estimated.'
    }
    Write-AcceptanceJson $receiptPath $receipt
    return $receipt
}
