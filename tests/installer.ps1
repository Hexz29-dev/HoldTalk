param([Parameter(Mandatory = $true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{462F8798-9257-4702-B8D9-7217A480E739}_is1'
if (Test-Path -LiteralPath $registration) { throw 'Uninstall the installed HoldTalk before this isolated installer test.' }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$target = Join-Path $artifacts ('installer-test-' + [Guid]::NewGuid().ToString('N'))
$group = 'HoldTalk Installer Test ' + [Guid]::NewGuid().ToString('N')
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$group\HoldTalk.lnk"
$uninstaller = Join-Path $target 'unins000.exe'

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}

function Install([string]$LogName) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=',
        ('/DIR="{0}"' -f $target), ('/GROUP="{0}"' -f $group),
        ('/LOG="{0}"' -f (Join-Path $artifacts $LogName)))
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Check ($process.ExitCode -eq 0) 'Installer completed successfully'
}

try {
    Install 'installer-first.log'
    Check (Test-Path -LiteralPath $registration) 'Registered in Windows installed apps'
    Check (Test-Path -LiteralPath $shortcut) 'Start menu shortcut created'
    foreach ($relative in @('Holdtalk.exe', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'tools\ffmpeg\bin\ffmpeg.exe',
        'tools\transcribe-vulkan\transcribe.dll', 'tools\transcribe-vulkan\ggml-vulkan.dll',
        'tools\ffmpeg\LICENSE', 'third-party\cohere-model-LICENSE.txt')) {
        Check (Test-Path -LiteralPath (Join-Path $target $relative)) "Payload contains $relative"
    }
    $model = Join-Path $target 'models\cohere-transcribe-03-2026-Q4_K_M.gguf'
    Check ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -eq
        '0ea56826d8bd5d74b7143a4a04e022dc1bb75452cfae49d98b6acb0c1d16a1fb') 'Installed model matches pinned SHA256'
    Check ((Get-ChildItem -LiteralPath (Join-Path $target 'models') -File).Count -eq 1) 'Only the default model is packaged'
    $configPath = Join-Path $target 'config.json'
    Check ((Get-Content -LiteralPath $configPath -Raw) -eq
        (Get-Content -LiteralPath (Join-Path $root 'config.example.json') -Raw)) 'Fresh install uses default settings'
    Check (-not (Test-Path -LiteralPath (Join-Path $target '.env'))) 'No local API keys are packaged'
    Check (-not (Test-Path -LiteralPath (Join-Path $target 'config.before-cohere.json'))) 'No local configuration backups are packaged'
    & (Join-Path $target 'tools\ffmpeg\bin\ffmpeg.exe') -version | Select-Object -First 1
    Check ($LASTEXITCODE -eq 0) 'Installed FFmpeg runs'

    $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $settings.language = 'en'
    $settings | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
    $envPath = Join-Path $target '.env'
    Set-Content -LiteralPath $envPath -Value '# installer test; no credentials'
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    $envHash = (Get-FileHash -LiteralPath $envPath).Hash
    Install 'installer-update.log'
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Update preserves settings'
    Check ((Get-FileHash -LiteralPath $envPath).Hash -eq $envHash) 'Update preserves .env'
}
finally {
    if (Test-Path -LiteralPath $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -WindowStyle Hidden -Wait -PassThru
        Check ($process.ExitCode -eq 0) 'Uninstaller completed successfully'
        Check (-not (Test-Path -LiteralPath $registration)) 'Uninstall removes Windows registration'
        Check (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall removes Start menu shortcut'
        Check (-not (Test-Path -LiteralPath (Join-Path $target 'Holdtalk.exe'))) 'Uninstall removes application binaries'
        Check (-not (Test-Path -LiteralPath (Join-Path $target 'models'))) 'Uninstall removes bundled model'
        Check (Test-Path -LiteralPath (Join-Path $target 'config.json')) 'Uninstall preserves settings'
        Check (Test-Path -LiteralPath (Join-Path $target '.env')) 'Uninstall preserves .env'
    }
    # The target is a unique directory created by this test, inside artifacts.
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($target))) -ne [IO.Path]::GetFullPath($artifacts)) {
        throw 'Refusing to remove a test directory outside artifacts.'
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
