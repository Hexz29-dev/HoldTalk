#define PublishDir "..\dist\installer-app"
#define PreparedDir "..\dist\Holdtalk"
#define AppVersion GetVersionNumbersString(PublishDir + "\Holdtalk.exe")
#ifndef VcRuntimeDir
  #error Pass /DVcRuntimeDir with the x64 Visual C++ CRT redistributable directory.
#endif

[Setup]
AppId={{462F8798-9257-4702-B8D9-7217A480E739}
AppName=HoldTalk
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\HoldTalk
DefaultGroupName=HoldTalk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupMutex=HoldTalkSetup
UninstallDisplayIcon={app}\Holdtalk.exe
SetupIconFile=..\src\Assets\HoldTalk.ico
OutputDir=..\dist\installer
OutputBaseFilename=HoldTalk-Setup-{#AppVersion}-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Holdtalk.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#VcRuntimeDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; DestName: "config.json"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\.env.example"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\third-party\*"; DestDir: "{app}\third-party"; Flags: ignoreversion recursesubdirs
Source: "{#PreparedDir}\tools\ffmpeg\bin\ffmpeg.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "{#PreparedDir}\tools\ffmpeg\LICENSE"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "{#PreparedDir}\tools\ffmpeg\README.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "{#PreparedDir}\tools\transcribe-vulkan\*.dll"; DestDir: "{app}\tools\transcribe-vulkan"; Flags: ignoreversion
Source: "{#PreparedDir}\tools\transcribe-vulkan\licenses\*"; DestDir: "{app}\tools\transcribe-vulkan\licenses"; Flags: ignoreversion recursesubdirs
Source: "{#PreparedDir}\models\cohere-transcribe-03-2026-Q4_K_M.gguf"; DestDir: "{app}\models"; Hash: "0ea56826d8bd5d74b7143a4a04e022dc1bb75452cfae49d98b6acb0c1d16a1fb"; Flags: ignoreversion nocompression

[Icons]
Name: "{group}\HoldTalk"; Filename: "{app}\Holdtalk.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\HoldTalk"; Filename: "{app}\Holdtalk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Holdtalk.exe"; Description: "{cm:LaunchProgram,HoldTalk}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
