# Holdtalk for Windows

A native Windows app with a glass-style dashboard and tray icon for push-to-talk
dictation. Hold **right Ctrl** to record; release it to transcribe with
**Cohere Transcribe 03-2026** and paste into the focused
field. Hold **F8** instead for optional punctuation/filler cleanup.

Windows 10/11 x64. No accounts, telemetry, cloud speech APIs, web UI, or app
packages. The dashboard uses WPF and the tray uses WinForms. Recording uses
FFmpeg **DirectShow**, a Windows keyboard hook, and `SendInput` for Ctrl+V.

## Run the prepared app

1. On a fresh checkout, [build and download dependencies](#build-and-download-dependencies)
   first. Pause other dictation apps if they use the same hotkeys.
2. Open `dist\Holdtalk\Holdtalk.exe` (or double-click `Start-Holdtalk.cmd`).
3. Focus a text field, hold **right Ctrl**, speak, then release. Wait for the tray
   icon to return to idle before starting another dictation. Keep your intended
   field focused until paste finishes.
4. Left-click the tray icon to open the dashboard. **Início** shows the current
   state, shortcut, selected microphone and latest transcript. **Histórico** lists
   your last ten transcripts with search and **Copiar** actions. Right-click the
   tray icon for the compact menu. Copy deliberately replaces the clipboard.
5. In **Configurações**, choose a microphone and language, then click **Salvar
   configurações**. These changes apply to the next dictation immediately.
   Advanced settings still use the config file and require a restart.

Click the microphone in the header to jump to its settings. History search
includes a clear button and result count. **Atalhos e processamento** expands
to show shortcuts, runtime details and advanced configuration links. Messages
can be dismissed without leaving the current page. Short windows prioritize
the dictation status and latest transcript; processing keeps the same layout.

The window opens at startup. Its close button hides it and keeps hotkeys active
in the tray. Use **Encerrar HoldTalk** to stop the app. Resize the window as
needed; longer pages scroll while navigation and pause controls stay visible.

The icon shows a blue microphone for idle, a red record symbol while recording,
and an amber clock while transcribing/cleaning/pasting. Hover for a text status.
**Pause hotkeys** restores normal use of the configured keys. Quit stops the app.
The icon may initially be inside Windows' hidden tray icons; pin it if desired.

## Windows installer

The installer is generated in `dist\installer\HoldTalk-Setup-<version>-x64.exe`.
It includes .NET, the Visual C++ runtime, FFmpeg and the Cohere runtime, without the voice model.
Run it on Windows 10 (build 19041 or later) / Windows 11 x64; no administrator
account or .NET installation is needed. The installer opens
in Portuguese on Brazilian Windows and also supports English.

After installation finishes, leave **Launch HoldTalk** selected to open the app.
If the default model is missing, HoldTalk asks whether to download it (1.56 GB),
with progress and cancellation. You can decline and reopen HoldTalk later to retry.
Internet is required for this download; dictation works offline afterward.
The pinned revision and SHA256 are verified before the model is used. Existing
models and custom `modelPath` settings are left untouched.

HoldTalk installs to `%LOCALAPPDATA%\Programs\HoldTalk`, with a Start menu shortcut
and an optional desktop shortcut. Quit HoldTalk from its tray menu before
installing, updating or uninstalling. Uninstall it through Windows Settings.
Updates preserve `config.json`, `.env` and downloaded models; uninstall also preserves those files
and the transcript archive in `%USERPROFILE%\Dictation`. The installer ships
default settings and never packages your local configuration or API keys.
The build is unsigned and may trigger a Windows SmartScreen warning.

To generate the installer, prepare the dependencies below with `-SelfContained -SkipModel`, install
[Inno Setup 6](https://jrsoftware.org/isdl.php) and the Visual Studio C++ x64
build tools (for the redistributable CRT files), then run:

```powershell
powershell -NoProfile -File .\scripts\build-installer.ps1
# For a compiler installed elsewhere:
# powershell -NoProfile -File .\scripts\build-installer.ps1 -IsccPath 'C:\path\ISCC.exe'
# Or provide the Visual C++ redistributable directory explicitly:
# powershell -NoProfile -File .\scripts\build-installer.ps1 -VcRuntimePath 'C:\path\x64\Microsoft.VC143.CRT'
```

Compilation uses a separate `dist\installer-app` folder, so the portable app
can remain running. Only the current runtime is packaged;
local models and old Whisper dependencies are excluded.
The Visual C++ DLLs are deployed beside the app, following Microsoft's
[local deployment guidance](https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp?view=msvc-170).
Keep these redistributable files updated when rebuilding the installer.

Test installation, upgrade and removal in an isolated folder (requires no existing
installed HoldTalk; the portable app can remain open):

```powershell
powershell -NoProfile -File .\tests\installer.ps1 -InstallerPath .\dist\installer\HoldTalk-Setup-1.0.0.0-x64.exe
```

## Build and download dependencies

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then
run in this repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1 -SelfContained
```

This downloads FFmpeg 9.0.2, the CPU/Vulkan native library from
[transcribe.cpp v0.4.0](https://github.com/handy-computer/transcribe.cpp/releases/tag/v0.4.0),
and **CohereLabs/cohere-transcribe-03-2026** in Q4_K_M GGUF format (1.56 GB).
The [GGUF conversion](https://huggingface.co/handy-computer/cohere-transcribe-03-2026-gguf)
is published by transcribe.cpp's maintainers from the original Cohere checkpoint.
The model revision and SHA256 hashes are pinned. No Python installation or cloud
speech service is required. Native licenses ship beside the DLLs; the model is
Apache-2.0 licensed.

The script creates a portable app folder under `dist\Holdtalk`. It migrates an
existing Whisper config to Cohere while preserving microphone, hotkeys and other
preferences, and saves the original as `config.before-cohere.json`. An existing
Cohere configuration and `.env` are preserved. Automatic language selection from
an old config becomes Portuguese; recordings are capped at 400 seconds.
Quit Holdtalk before running setup. The self-contained build includes .NET;
omit `-SelfContained` to use an installed .NET 10 Desktop runtime.

Internet is needed only for setup. **Plain dictation works offline afterward**.
Move the entire `dist\Holdtalk` folder together, including `tools` and `models`.
For a Cohere GGUF you already have, use `-SkipModel` and set `modelPath`.
The default is `models/cohere-transcribe-03-2026-Q4_K_M.gguf`, SHA256
`0ea56826d8bd5d74b7143a4a04e022dc1bb75452cfae49d98b6acb0c1d16a1fb`.
Whisper GGML and Hugging Face safetensors are incompatible with this app's
Cohere runtime.

Cohere supports 14 explicit languages; **it has no automatic language detection**.
Portuguese (`pt`) is the default. Choose another language in the dashboard before
dictating. The model accepts at most **400 seconds per recording**; Holdtalk's
normal limit remains 300 seconds. A hidden worker loads the model once and
warms the GPU before dictation is enabled. Keep Holdtalk open to reuse the
loaded model. Startup takes a few seconds; subsequent dictations skip loading.
The worker keeps model weights and GPU buffers resident until you quit.
Vulkan automatically selects a compatible GPU and falls back to CPU when no
GPU is available. A working graphics driver is required for GPU inference.
The worker requests device-local Vulkan memory without preferring host-visible
VRAM (`GGML_VK_DISABLE_HOST_VISIBLE_VIDMEM=1`, set before the worker starts).
This avoids a measured Radeon slowdown on larger encoder graphs; Vulkan's
integrated-GPU/UMA memory policy and CPU fallback remain available.

## Microphone and accessibility permissions

On Windows 11, open **Settings → Privacy & security → Microphone** and enable
**Microphone access** and **Let desktop apps access your microphone**. On Windows
10 these are under **Settings → Privacy → Microphone**. The tray menu opens this
settings page. See Microsoft's
[microphone permissions guide](https://support.microsoft.com/en-us/windows/privacy/turn-on-app-permissions-for-your-microphone-in-windows).

**First run may trigger Windows microphone/security permission prompts**, depending
on your Windows version and policies. Grant microphone access when offered.
An unsigned local build can also show a SmartScreen warning. Windows desktop apps
commonly use the global desktop-microphone setting instead of a per-app prompt;
there is no guaranteed first-run permission dialog.

Windows has **no separate macOS-style Accessibility permission** to grant for this
keyboard hook and paste simulation. Run Holdtalk normally. Windows restricts
[SendInput into apps with higher privileges](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput);
an elevated editor requires matching privileges, or you can copy the transcript
from history and paste manually. Secure desktops, password prompts and fields
that reject paste cannot be dictated into. Do not disable Windows security.

## Configuration and choosing a microphone

Microphone and language can be changed in the dashboard. For advanced settings,
edit `config.json` **beside Holdtalk.exe**, then quit/reopen the app. Relative
paths resolve beside the executable, independent of your working directory.
`config.example.json` documents every default:

When running a development build from `src\bin` inside this checkout, Holdtalk
uses the prepared `dist\Holdtalk` folder for config, models, dependencies and
`.env` if its config exists. This makes `dotnet run` use the same downloaded
model as the portable app. For normal use, launch `Start-Holdtalk.cmd` or
`dist\Holdtalk\Holdtalk.exe`.

| Setting | Default / meaning |
| --- | --- |
| `ffmpegPath` | `tools/ffmpeg/bin/ffmpeg.exe` |
| `transcribeLibraryPath` | `tools/transcribe-vulkan/transcribe.dll` (version 0.4.0 required) |
| `modelPath` | `models/cohere-transcribe-03-2026-Q4_K_M.gguf` |
| `microphone` | Empty selects the Windows default recording device; set the exact DirectShow name to choose another |
| `dictationKey` | `RControlKey` |
| `cleanupKey` | `F8` |
| `language` | `pt`; one of `pt`, `en`, `fr`, `de`, `es`, `it`, `nl`, `pl`, `el`, `ar`, `ja`, `zh`, `vi`, `ko` |
| `maxRecordingSeconds` | `300`; range 1?400; safety stop for a stuck/missed release |
| `transcriptionTimeoutSeconds` | `300`; kills a hung transcription process and its children |
| `clipboardRestoreDelayMs` | `500`; allow the focused app to consume Ctrl+V before restoring |

List microphone names with:

```powershell
.\dist\Holdtalk\tools\ffmpeg\bin\ffmpeg.exe -hide_banner -list_devices true -f dshow -i dummy
```

Use a name marked `(audio)`. FFmpeg intentionally exits with an error after this
listing. The empty setting selects the **Windows default multimedia recording
device**, read through Core Audio and matched to DirectShow. Change it in Windows
Sound settings to choose your normal microphone. Unplugged/changed default devices
need an app restart; an explicit name overrides the default.
The tray menu and startup notification show the microphone in use. Fully silent audio is rejected
before Cohere runs, rather than producing an invented transcript; very quiet
speech remains allowed. This detects digital silence, not background noise.
Hotkeys may be left/right Ctrl or Shift (`RControlKey`, `LControlKey`,
`RShiftKey`, `LShiftKey`), or `F1`–`F24`. Each is a single **hold** key, not a toggle
or chord. The two keys must differ. Captured keys are reserved while the app is
active; unconfigured keys pass through. Hotkeys are ignored during transcription.

FFmpeg starts when you press the key, so allow a brief microphone startup moment
before speaking. Very short taps (under about 0.1 seconds of captured audio) are
discarded. Release modifier keys before paste. If another app consumes clipboard
contents slowly, increase `clipboardRestoreDelayMs`.

## Optional LLM cleanup

Copy `.env.example` to `.env` beside the executable, set `LLM_API_KEY`, and restart:

```dotenv
LLM_API_KEY=your-key
LLM_ENDPOINT=https://api.openai.com/v1/chat/completions
LLM_MODEL=gpt-4.1-mini
```

The request uses the documented
[Chat Completions format](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create).
Any compatible endpoint/model can be configured. HTTPS is required for remote
endpoints; HTTP is accepted only for loopback/local servers. Redirects are disabled
so a server cannot forward your API key elsewhere. No key means **F8 does plain
dictation with zero network requests**. Errors, unavailable models, empty results,
and a 20-second request timeout all fall back to the original transcript.

**Only F8 cleanup sends transcript text to your configured provider; audio stays
local.** A hosted provider may require an account and charge for that optional
request. To keep cleanup offline, use an OpenAI-compatible local LLM server, set
its endpoint/model, and put a nonempty placeholder key such as `local` in `.env`.
Keep `.env` private. It is excluded from git. Cleanup can alter wording; check
important text.

## History, clipboard and audio

Every completed transcript is appended to
`%USERPROFILE%\Dictation\YYYY-MM.md` with a local timestamp and timezone. History
survives restart and the tray reloads its newest ten entries. The archive stores
the final text (cleaned when cleanup succeeds). Paste failure keeps the transcript
in history. A write failure warns you and leaves it in this session's tray history.

The previous clipboard is materialized before Ctrl+V and restored afterward,
including ordinary text, HTML, file lists and images. If you copy something else
during that interval, your new copy wins. An unsupported proprietary clipboard
format causes paste to be skipped to protect your existing contents; use Copy
from history. Clipboard managers/history can still observe the temporary text.
Restoration uses a timed delay because arbitrary target apps do not acknowledge
paste completion.

Audio is a temporary mono **16 kHz PCM WAV** in `%TEMP%`, removed immediately
after transcription, including on normal error/cancellation/quit paths. The
transcript returns through a private process pipe, without an extra text file.
A forced process kill, crash, power loss, or filesystem permission failure can leave `holdtalk-*.wav` files in
`%TEMP%`; deletion failure warns you. They are never uploaded or retained as a
feature. The Markdown archive is plaintext: avoid dictating secrets into it if
that is unsuitable for your workflow.

## Checks

No test framework packages are needed:

```powershell
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release
# Isolated Windows clipboard and Ctrl+V checks; briefly opens a test text box:
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release -- --windows
# Dashboard interactions, settings persistence and rendered previews:
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release -- --ui "$PWD\artifacts\ui"
```

Tests cover hold/release/repeat behavior, configuration validation, offline and
failed cleanup, monthly history, process failure/timeouts, and clipboard formats.
The image checks include native screenshot-style bitmaps, which can require a
Win32 fallback when the managed clipboard conversion is unavailable.
The Windows checks refuse to send Ctrl+V unless their own test window owns focus.
The optional real speech test uses the synthetic Portuguese fixture in
`tests/fixtures/portuguese.wav` when configured for `pt`; otherwise it uses
whisper.cpp's public JFK sample. The Portuguese fixture was generated with
Windows' Microsoft Maria voice, not recorded from a user. The test briefly
captures the configured microphone to verify WAV formatting and release.
It deletes the captured audio without transcribing it:

```powershell
New-Item -ItemType Directory -Path .\.downloads -Force
curl.exe --fail --location --output .\.downloads\jfk.wav https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.9.2/samples/jfk.wav
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release -- --speech "$PWD\dist\Holdtalk"
```

With `jfk.wav` downloaded, compare CPU and Vulkan on the same Cohere model:

```powershell
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release -- --benchmark "$PWD\dist\Holdtalk"
# Measure WAV-to-text latency using the configured language:
dotnet run --project .\tests\Holdtalk.Tests.csproj -c Release -- --latency "$PWD\dist\Holdtalk"
```

The speech check includes actual model recognition, silence rejection and
microphone format/release checks. Inference runs in a persistent hidden child process;
errors, truncated output and empty results fail rather than pasting incomplete
text. Timeout, cancellation and quit kill native inference and its children. A failed
worker reloads on the next request. The worker reads and converts PCM in bulk
once, rejects digital silence before inference, and stays warm after silence.
`--latency` reports WAV reading, mel, encoder and decoder timings, the first
request of each length, and median/sample p95 over five subsequent requests.
These timings start with a finalized WAV; they exclude microphone shutdown,
optional cleanup and clipboard delivery.

On 2026-10-15, RX 6650 XT Q4_K_M tests of synthetic Portuguese clips measured
warm WAV-to-text medians of **202/270/325/453 ms** for **5/10/15/30 seconds**
of audio (five runs per length). The controlled native comparison reduced
10/15-second medians from approximately **1,074/1,522 ms to 267/325 ms**, with
identical complete outputs on these fixtures. Short 5-second clips stayed
around 190–200 ms. Results depend on hardware, drivers, audio and GPU load;
these samples are not a latency guarantee or a broad speech-quality evaluation.
Optional F8 cloud cleanup adds its own latency.
