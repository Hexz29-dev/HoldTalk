$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'HoldTalk-identidade\variantes'
$destination = Join-Path $root 'src\Assets'
$culture = [Globalization.CultureInfo]::InvariantCulture

# Only the path, rounded-rectangle and group shapes used by the supplied brand are supported.
function Convert-Group($node, [string]$inheritedFill = 'none') {
    $group = New-Object Windows.Media.DrawingGroup
    $fill = if ($node.HasAttribute('fill')) { $node.GetAttribute('fill') } else { $inheritedFill }
    foreach ($child in $node.ChildNodes) {
        if ($child.LocalName -in 'title', 'desc', '') { continue }
        if ($child.LocalName -eq 'g') { $group.Children.Add((Convert-Group $child $fill)); continue }
        switch ($child.LocalName) {
            'path' { $geometry = [Windows.Media.Geometry]::Parse('F1 ' + $child.GetAttribute('d')) }
            'rect' {
                $values = @('x', 'y', 'width', 'height') | ForEach-Object { [double]::Parse($child.GetAttribute($_), $culture) }
                $radius = if ($child.HasAttribute('rx')) { [double]::Parse($child.GetAttribute('rx'), $culture) } else { 0 }
                $geometry = New-Object Windows.Media.RectangleGeometry ([Windows.Rect]::new($values[0], $values[1], $values[2], $values[3])), $radius, $radius
            }
            default { throw "Unsupported brand element: $($child.LocalName)" }
        }
        $color = if ($child.HasAttribute('fill')) { $child.GetAttribute('fill') } else { $fill }
        if ($color -eq 'none') { throw 'Brand shape has no fill.' }
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($color)
        $group.Children.Add([Windows.Media.GeometryDrawing]::new($brush, $null, $geometry))
    }
    if ($node.HasAttribute('transform')) {
        if ($node.GetAttribute('transform') -ne 'translate(24 48) scale(.40625)') { throw 'Unsupported brand transform.' }
        $transforms = New-Object Windows.Media.TransformGroup
        $transforms.Children.Add([Windows.Media.ScaleTransform]::new(.40625, .40625))
        $transforms.Children.Add([Windows.Media.TranslateTransform]::new(24, 48))
        $group.Transform = $transforms
    }
    return $group
}

$resources = New-Object Windows.ResourceDictionary
foreach ($asset in @(@('BrandSymbol', 'HoldTalk-icone.svg'), @('BrandLogoWhite', 'HoldTalk-logo-branco.svg'))) {
    [xml]$svg = Get-Content -LiteralPath (Join-Path $source $asset[1]) -Encoding UTF8 -Raw
    $drawing = Convert-Group $svg.DocumentElement
    if ($drawing.Bounds.IsEmpty) { throw 'Empty brand asset.' }
    $resources.Add($asset[0], [Windows.Media.DrawingImage]::new($drawing))
}
$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Indent = $true
$xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create((Join-Path $destination 'Brand.xaml'), $xmlSettings)
try { [Windows.Markup.XamlWriter]::Save($resources, $writer) } finally { $writer.Dispose() }

$frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try { $context.DrawImage($resources['BrandSymbol'], [Windows.Rect]::new(($size / 16), ($size / 16), ($size * 14 / 16), ($size * 14 / 16))) } finally { $context.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    try { $encoder.Save($stream); [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() } } finally { $stream.Dispose() }
}
$iconWriter = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $destination 'HoldTalk.ico')))
try {
    $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $iconWriter.Write([byte]$dimension); $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$frame.Bytes.Length); $iconWriter.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $iconWriter.Write([byte[]]$frame.Bytes) }
} finally { $iconWriter.Dispose() }
Write-Host 'Exported native brand vectors and a seven-size Windows icon.'
