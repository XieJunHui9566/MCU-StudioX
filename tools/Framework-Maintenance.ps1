# 明确已有输入，先拒绝缺项；本脚本不下载工具、不连接硬件。
function Read-StudioXFrameworkInputs([string]$Path, [ValidateSet('pic', 'arduino')][string]$Area)
{
    $plan = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($plan.formatVersion -ne 1)
    {
        throw "Unsupported $Area maintenance input format."
    }
    $fields = if ($Area -eq 'pic')
    {
        @{runtime ='Container';
            pack  ='Leaf'
        }
    }
    else
    {
        @{runtime    ='Container';
            avrPack  ='Leaf';
            espPacks ='Container'
        }
    }
    foreach ($field in $fields.Keys)
    {
        $value = $plan.$field
        if ($value -isnot [string] -or ![IO.Path]::IsPathFullyQualified($value) -or !(Test-Path -LiteralPath $value -PathType $fields[$field]))
        {
            throw "$Area maintenance requires an explicit existing absolute $field path."
        }
    }
    $tools = Join-Path $plan.runtime 'toolsets'
    $identities = if ($Area -eq 'pic')
    {
        @('microchip.xc8/4.0.0')
    }
    else
    {
        @('arduino.avr/1.8.8', 'arduino.esp32/3.3.8')
    }
    foreach ($identity in $identities)
    {
        if (!(Test-Path -LiteralPath (Join-Path $tools "$identity/toolset.json") -PathType Leaf))
        {
            throw "Missing managed component: $identity"
        }
    }
    if ($Area -eq 'arduino')
    {
        if ($plan.PSObject.Properties.Name -contains 'cacheData')
        {
            $boundary = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($Path)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if ($plan.cacheData -isnot [string] -or ![IO.Path]::IsPathFullyQualified($plan.cacheData) -or
                ![IO.Path]::GetFullPath($plan.cacheData).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
                !(Test-Path -LiteralPath $plan.cacheData -PathType Container))
            {
                throw 'cacheData must be an existing isolated validation directory beneath the input plan directory.'
            }
        }
        foreach ($name in @('esp32-wroom-32', 'esp32c3', 'esp32c5', 'esp32c6', 'esp32s3', 'esp32p4'))
        {
            if (!(Test-Path -LiteralPath (Join-Path $plan.espPacks "espressif.$name-0.1.1.mcupack") -PathType Leaf))
            {
                throw "Missing explicit Arduino framework matrix pack: $name"
            }
        }
    }
    return $plan
}

function Invoke-StudioXFrameworkMaintenance([string]$Area, [string]$InputFile, [string]$Build, [string]$Output)
{
    $plan = Read-StudioXFrameworkInputs $InputFile $Area
    if ($Area -eq 'pic')
    {
        & (Join-Path $Build 'bin/StudioX.PicValidation/release_win-x64/StudioX.PicValidation.exe') $Output $plan.runtime $plan.pack
    }
    else
    {
        $data = if ($plan.PSObject.Properties.Name -contains 'cacheData')
        {
            $plan.cacheData
        }
        else
        {
            Join-Path $Output 'data'
        }
        # 原生命令的字符串展开会拆成字符，必须明确为字符串数组以保留完整开关。
        [string[]]$extra = if ($plan.PSObject.Properties.Name -contains 'cacheData')
        {
            @('--reuse-data')
        }
        else
        {
            @()
        }
        & (Join-Path $Build 'bin/StudioX.ArduinoTemplateValidation/release_win-x64/StudioX.ArduinoTemplateValidation.exe') $Output $plan.runtime $data $plan.avrPack $plan.espPacks @extra
    }
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Area framework validation failed: $LASTEXITCODE"
    }
    $result = Get-Content -LiteralPath (Join-Path $Output 'result.json') -Raw | ConvertFrom-Json
    if ($result.passed -isnot [bool] -or !$result.passed -or $result.hardware -isnot [bool] -or $result.hardware)
    {
        throw "$Area requires actual successful software evidence; hardware is outside this maintenance domain."
    }
    $desktop = Join-Path $Build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
    $ui = Join-Path $Output 'ui'
    $arguments = if ($Area -eq 'pic')
    {
        @('--preview-pic', $ui, $plan.runtime, $plan.pack, $result.projects.'xc8-gpio-4mhz')
    }
    else
    {
        @('--preview-arduino', $ui, $result.projects.uno, $plan.runtime)
    }
    # Start-Process 在 Windows 需要为带空格路径保留引号；所有值来自已核验的绝对路径。
    $arguments = @($arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $desktop -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000))
    {
        $process.Kill($true);
        throw "$Area UI validation timed out."
    }
    if ($process.ExitCode -ne 0)
    {
        throw "$Area UI validation failed: $($process.ExitCode)"
    }
    $summary = if ($Area -eq 'pic')
    {
        'result.json'
    }
    else
    {
        'arduino-ui.json'
    }
    $uiResult = Get-Content -LiteralPath (Join-Path $ui $summary) -Raw | ConvertFrom-Json
    if ($uiResult.passed -isnot [bool] -or !$uiResult.passed -or $uiResult.hardware -isnot [bool] -or $uiResult.hardware)
    {
        throw "$Area UI requires actual successful offline evidence."
    }
}
