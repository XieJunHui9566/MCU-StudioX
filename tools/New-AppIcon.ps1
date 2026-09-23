param([string]$PreviewDirectory)
# 矢量母版是唯一来源；不依赖字体、浏览器或额外绘图软件。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/StudioX.Desktop/Assets'))
$brand = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assetDirectory 'StudioXBrand.xaml')))
function ConvertTo-Png([Windows.Media.ImageSource]$Source, [int]$Size) {
    $visual = [Windows.Media.DrawingVisual]::new(); $context = $visual.RenderOpen()
    $context.DrawImage($Source, [Windows.Rect]::new(0,0,$Size,$Size)); $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
$frames = foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    @{ Size = $size; Bytes = (ConvertTo-Png $brand['StudioXAppIcon'] $size) }
}
$destination = Join-Path $assetDirectory 'StudioX.ico'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($destination))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $edge = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'StudioX.png'), (ConvertTo-Png $brand['StudioXAppIcon'] 256))
if ($PreviewDirectory) {
    $preview = [IO.Path]::GetFullPath($PreviewDirectory)
    [IO.Directory]::CreateDirectory($preview) | Out-Null
    foreach ($frame in $frames) { [IO.File]::WriteAllBytes((Join-Path $preview "icon-$($frame.Size).png"), $frame.Bytes) }
    [IO.File]::WriteAllBytes((Join-Path $preview 'mark-256.png'), (ConvertTo-Png $brand['StudioXMark'] 256))
    # 原尺寸标题栏与深浅底对照，不用放大图代替小尺寸可读性检查。
    $visual = [Windows.Media.DrawingVisual]::new(); $context = $visual.RenderOpen()
    function Brush([string]$Color) { [Windows.Media.BrushConverter]::new().ConvertFromString($Color) }
    function Label([string]$Text, [double]$X, [double]$Y, [double]$Size, [string]$Color) {
        $textRun = [Windows.Media.FormattedText]::new($Text, [Globalization.CultureInfo]::InvariantCulture,
            [Windows.FlowDirection]::LeftToRight, [Windows.Media.Typeface]::new('Segoe UI'), $Size, (Brush $Color), 1.0)
        $context.DrawText($textRun, [Windows.Point]::new($X,$Y))
    }
    $context.DrawRectangle((Brush '#111923'), $null, [Windows.Rect]::new(0,0,760,340))
    $context.DrawImage($brand['StudioXAppIcon'], [Windows.Rect]::new(40,40,176,176))
    Label 'MCU StudioX' 250 48 30 '#E6EDF7'
    Label 'Application icon' 252 92 14 '#94A4BA'
    $context.DrawRoundedRectangle((Brush '#252A34'), $null, [Windows.Rect]::new(250,130,460,48),6,6)
    $context.DrawImage($brand['StudioXMark'], [Windows.Rect]::new(261,140,28,28))
    Label 'MCU StudioX' 301 144 13 '#DFE1E5'; Label '/   main.c' 401 144 13 '#8E99AE'
    $context.DrawRoundedRectangle((Brush '#EEF2F7'), $null, [Windows.Rect]::new(250,190,460,48),6,6)
    $context.DrawImage($brand['StudioXMark'], [Windows.Rect]::new(261,200,28,28))
    Label 'MCU StudioX' 301 204 13 '#29384D'; Label '/   main.c' 401 204 13 '#738094'
    $x = 46
    foreach ($size in @(16,20,24,32,48)) {
        $context.DrawImage($brand['StudioXAppIcon'], [Windows.Rect]::new($x,275-$size/2,$size,$size))
        Label "$size" $x 307 11 '#8B9DB4'; $x += 66
    }
    Label 'Windows / Title bar / Welcome' 420 270 12 '#94A4BA'
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(760,340,96,96,[Windows.Media.PixelFormats]::Pbgra32); $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file = [IO.File]::Create((Join-Path $preview 'brand-preview.png'))
    try { $encoder.Save($file) } finally { $file.Dispose() }
}
Write-Output $destination
