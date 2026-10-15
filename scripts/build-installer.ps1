param([string]$IsccPath, [string]$VcRuntimePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $root '.downloads\InnoSetup6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Install Inno Setup 6 (https://jrsoftware.org/isdl.php), or pass -IsccPath to ISCC.exe.'
}
if (-not $VcRuntimePath) {
    $VcRuntimePath = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT') `
        -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
foreach ($dll in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    if (-not $VcRuntimePath -or -not (Test-Path -LiteralPath (Join-Path $VcRuntimePath $dll) -PathType Leaf)) {
        throw 'Install the Visual Studio C++ x64 build tools, or pass -VcRuntimePath to the x64 Microsoft.VC*.CRT redistributable folder.'
    }
}
$VcRuntimePath = (Resolve-Path -LiteralPath $VcRuntimePath).Path

$prepared = Join-Path $root 'dist\Holdtalk'
foreach ($relative in @(
    'tools\ffmpeg\bin\ffmpeg.exe',
    'tools\transcribe-vulkan\transcribe.dll'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $prepared $relative) -PathType Leaf)) {
        throw "Missing $relative. Run scripts\setup.ps1 -SelfContained -SkipModel first."
    }
}

# Publish separately so packaging does not overwrite the running portable app.
& dotnet publish (Join-Path $root 'src\Holdtalk.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $root 'dist\installer-app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

& $IsccPath "/DVcRuntimeDir=$VcRuntimePath" (Join-Path $root 'installer\HoldTalk.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Host "Ready: $root\dist\installer"
