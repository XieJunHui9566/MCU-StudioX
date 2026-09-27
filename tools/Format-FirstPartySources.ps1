param(
    [switch]$Write,
    [switch]$PrepareTools,
    [string]$PythonExecutable = 'python',
    [string[]]$ExcludeFile = @()
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $repository 'artifacts/validation/source-style-current'
$pythonArguments = @((Join-Path $PSScriptRoot 'FormatFirstPartySources.py'))
if ($Write)
{
    $pythonArguments += '--write'
}
if ($PrepareTools)
{
    $pythonArguments += '--prepare-tools'
}
& $PythonExecutable @pythonArguments
$pythonExitCode = $LASTEXITCODE
if ($pythonExitCode -notin @(0, 1))
{
    throw 'Python/C source style verification failed.'
}

$module = Join-Path $toolDirectory 'PSScriptAnalyzer/1.24.0/PSScriptAnalyzer.psd1'
if (!(Test-Path -LiteralPath $module -PathType Leaf))
{
    throw 'Run tools/Format-FirstPartySources.ps1 -PrepareTools first.'
}
Import-Module $module
$settings = Import-PowerShellDataFile (Join-Path $toolDirectory 'PSScriptAnalyzer/1.24.0/Settings/CodeFormatting.psd1')
# 仅使用排版规则；不改命令大小写、字符串、变量或脚本行为。
$settings.IncludeRules = [string[]]@($settings.IncludeRules | Where-Object { $_ -ne 'PSUseCorrectCasing' })
$settings.Rules.PSUseCorrectCasing.Enable = $false
$settings.Rules.PSPlaceOpenBrace.IgnoreOneLineBlock = $false
$settings.Rules.PSPlaceCloseBrace.IgnoreOneLineBlock = $false
$settings.Rules.PSPlaceOpenBrace.OnSameLine = $false

function Get-SourceTokens([string]$Source)
{
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0)
    {
        throw ($parseErrors | ForEach-Object { $_.ToString() } | Out-String)
    }
    return $tokens
}

function Get-ExecutableTokenSignature([string]$Source)
{
    $ignoredKinds = @('NewLine', 'Comment', 'EndOfInput', 'LineContinuation')
    $values = Get-SourceTokens $Source | Where-Object { $_.Kind.ToString() -notin $ignoredKinds } |
        ForEach-Object { $_.Kind.ToString() + [char]0 + $_.Text }
    return [string]::Join([char]1, [string[]]$values)
}

function Expand-StatementSeparators([string]$Source)
{
    $tokens = @(Get-SourceTokens $Source)
    $parenthesisDepth = 0
    $offsets = [Collections.Generic.List[int]]::new()
    foreach ($token in $tokens)
    {
        switch ($token.Kind.ToString())
        {
            { $_ -in @('LParen', 'AtParen', 'DollarParen') }
            {
                $parenthesisDepth++
            }
            'RParen'
            {
                $parenthesisDepth--
            }
            'Semi'
            {
                # for 的三个表达式仍保留在括号中；块内相邻语句展开成独立行。
                if ($parenthesisDepth -eq 0 -and $Source.Substring($token.Extent.EndOffset) -notmatch '^\s*\r?\n')
                {
                    $offsets.Add($token.Extent.EndOffset)
                }
            }
        }
    }
    foreach ($offset in $offsets | Sort-Object -Descending)
    {
        $Source = $Source.Insert($offset, "`n")
    }
    return $Source
}

function Remove-OuterTrailingWhitespace([string]$Source)
{
    $strings = @(Get-SourceTokens $Source | Where-Object { $_.Kind.ToString() -match '^(String|HereString)' })
    $matches = @([regex]::Matches($Source, '(?m)[ \t]+(?=\r?$)'))
    foreach ($match in $matches | Sort-Object Index -Descending)
    {
        # here-string 可能承载生成源码或许可证，不能为了排版删掉其内部空格。
        $insideString = $strings | Where-Object {
            $_.Extent.StartOffset -le $match.Index -and $_.Extent.EndOffset -ge ($match.Index + $match.Length)
        }
        if (!$insideString)
        {
            $Source = $Source.Remove($match.Index, $match.Length)
        }
    }
    return $Source
}

$results = [Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File |
        Where-Object { $_.Name -notin $ExcludeFile } | Sort-Object Name)
{
    $original = [IO.File]::ReadAllText($file.FullName)
    $expanded = Expand-StatementSeparators $original
    $formatted = Invoke-Formatter -ScriptDefinition $expanded -Settings $settings
    $formatted = Remove-OuterTrailingWhitespace ($formatted.Replace("`r`n", "`n").TrimEnd() + "`n")
    if ((Get-ExecutableTokenSignature $original) -cne (Get-ExecutableTokenSignature $formatted))
    {
        throw "PowerShell executable tokens changed: $($file.Name)"
    }
    $changed = $original -cne $formatted
    if ($Write -and $changed)
    {
        [IO.File]::WriteAllText($file.FullName, $formatted, [Text.UTF8Encoding]::new($false))
    }
    $results.Add([ordered]@{
            file                       = [IO.Path]::GetRelativePath($repository, $file.FullName).Replace('\', '/')
            kind                       = 'PowerShell'
            changed                    = $changed
            executableContentIdentical = $true
        })
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $toolDirectory 'powershell-results.json') -Encoding utf8
$changedCount = @($results | Where-Object changed).Count
Write-Output "Verified $($results.Count) PowerShell files; $changedCount require formatting; write=$Write"
if (!$Write -and ($changedCount -gt 0 -or $pythonExitCode -ne 0))
{
    exit 1
}
