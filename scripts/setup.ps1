param([switch]$SkipModel, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\Holdtalk'
$cache = Join-Path $root '.downloads'
New-Item -ItemType Directory -Path $out, $cache -Force | Out-Null

function Download-Checked([string]$Url, [string]$Path, [string]$Hash) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash) { return }
    }
    Write-Host "Downloading $(Split-Path -Leaf $Path)..."
    $partial = "$Path.part"
    & curl.exe --fail --location --retry 3 --silent --show-error --output $partial $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) {
        Remove-Item -LiteralPath $partial
        throw "SHA256 verification failed: $Url"
    }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0' }
Write-Host 'Building Holdtalk...'
$publishArgs = @('publish', (Join-Path $root 'src\Holdtalk.csproj'), '-c', 'Release', '-o', $out, '--nologo')
if ($SelfContained) { $publishArgs += @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Versions and hashes are pinned; an upstream change cannot silently replace a binary.
$ffmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'
$ffmpegHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$ffmpegZip = Join-Path $cache 'ffmpeg-9.0.2.zip'
Download-Checked $ffmpegUrl $ffmpegZip $ffmpegHash
$ffmpegDir = Join-Path $out 'tools\ffmpeg'
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegDir 'bin\ffmpeg.exe'))) {
    $extract = Join-Path $cache 'ffmpeg-9.0.2'
    Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $extract -Force
    $package = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Path $ffmpegDir -Force | Out-Null
    Copy-Item -Path (Join-Path $package.FullName '*') -Destination $ffmpegDir -Recurse -Force
}

$transcribeArchive = Join-Path $cache 'transcribe-native-0.4.0-windows-x86_64-cpu-vulkan.tar.gz'
Download-Checked 'https://github.com/handy-computer/transcribe.cpp/releases/download/v0.4.0/transcribe-native-0.4.0-windows-x86_64-cpu-vulkan.tar.gz' `
    $transcribeArchive '9d5bd89f720d7979323e73f5220fe50fad5e38a7b0c2525225d453bd09adf2b0'
$transcribeDir = Join-Path $out 'tools\transcribe-vulkan'
New-Item -ItemType Directory -Path $transcribeDir -Force | Out-Null
& tar.exe -xf $transcribeArchive -C $transcribeDir --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'transcribe.cpp extraction failed.' }

if (-not $SkipModel) {
    $modelDir = Join-Path $out 'models'
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    Download-Checked 'https://huggingface.co/handy-computer/cohere-transcribe-03-2026-gguf/resolve/ab667acedcb5d8d56837ef3ed3568c3ad50dde47/cohere-transcribe-03-2026-Q4_K_M.gguf' `
        (Join-Path $modelDir 'cohere-transcribe-03-2026-Q4_K_M.gguf') '0ea56826d8bd5d74b7143a4a04e022dc1bb75452cfae49d98b6acb0c1d16a1fb'
}
$configPath = Join-Path $out 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $configPath
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.PSObject.Properties['whisperPath']) {
    $backup = Join-Path $out 'config.before-cohere.json'
    if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $configPath -Destination $backup }
    $config.PSObject.Properties.Remove('whisperPath')
    $config | Add-Member -NotePropertyName transcribeLibraryPath -NotePropertyValue 'tools/transcribe-vulkan/transcribe.dll' -Force
    $config.modelPath = 'models/cohere-transcribe-03-2026-Q4_K_M.gguf'
    if ($config.language -eq 'auto') { $config.language = 'pt' }
    $config.maxRecordingSeconds = [Math]::Min(400, $config.maxRecordingSeconds)
    [System.IO.File]::WriteAllText("$configPath.tmp", ($config | ConvertTo-Json) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
    Move-Item -LiteralPath "$configPath.tmp" -Destination $configPath -Force
}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $root 'third-party') -Destination $out -Recurse -Force
Write-Host "Ready: $out\Holdtalk.exe"
Write-Host 'Quit/pause Superwhisper before starting Holdtalk to avoid hotkey conflicts.'
Write-Host 'An empty microphone setting uses the Windows default recording device. See README to choose another.'
