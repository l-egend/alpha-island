# Alpha Island

Current version: **0.2.32**. The main installer contains no optional plugins. Their source and build scripts live in the separate sibling directory `alpha-island-plugins/`, with releases under `packages/<version>/`. Install optional plugin packages separately from Settings > Plugins. Main app updates preserve existing plugins and their settings. External translation starts disabled with an empty app name and shortcut.

[English](README.en.md) · [简体中文](README.md)

A compact Windows desktop panel for media, notifications, system status and everyday tools. Alpha Island stays as a small bar, expands into a pill for timely updates, and opens cards for detailed controls.

![Music and lyrics](docs/screens/music-lyrics.png)

## Features

- Media controls, cover artwork, an audio spectrum, synchronized lyrics and configurable lyric sources.
- A notification center with app filtering, grouped messages, actions, clearing and custom sounds.
- CPU, memory, network and power monitoring, plus battery levels from supported Bluetooth, receiver and controller devices.
- Screen OCR, a color picker, QR scanning, speech transcription and region translation.
- API chat, persona imports, voice commands and local agent integrations.
- Focus timers, local reminders, habit suggestions, system health alerts and productivity statistics.
- Clipboard history, a file shelf, quick launcher, welcome presets and optional plugins.
- Simplified Chinese and English interfaces, including built-in plugin settings.

Hardware readings depend on Windows, device protocols and plugins. AI, cloud speech and translation require a compatible service configured by you. Missing readings are shown as unavailable.

## Getting started

Requires Windows 10 version 2004 (19041) or later, or Windows 11. The x64 installer includes the required .NET runtime.

- Open Settings from the system tray.
- Choose **General → Interface language → English**, then **Restart app**. **Follow Windows** selects Chinese for a Chinese Windows display language and English otherwise.
- Click the side handle to switch components. Scroll within cards to scroll their content; scroll on the handle to switch cards.
- Hold the side handle to open the component menu. Right-click supported content to pin it.
- While a browser is maximized or fullscreen on the main display, the panel collapses automatically. Volume feedback stays visible. Click the bar to expand temporarily.

Language changes use local resources and do not call a translation API. Your messages, device names, lyrics, filenames and saved custom content are preserved. Imported welcome voices keep their original audio language. Third-party plugins supply their own text.

## Local reminders

Use the reminder window or API chat:

```text
Remind me to drink water in 30 minutes
Remind me tomorrow at 9 am to call Alex
Remind me to focus every weekday at 9:30
Remind me to take a break every 2 hours
List my reminders
Cancel reminder drink water
```

Keep Alpha Island running for timely delivery. Reminders are stored locally, and missed reminders are recorded after the application resumes. Ambiguous or invalid times require clarification.

## Build

Install the .NET 8 SDK and the Windows desktop development workload if using Visual Studio.

```powershell
git clone https://github.com/l-egend/alpha-island.git
cd alpha-island
dotnet restore alpha-island.sln
dotnet build alpha-island.sln -c Debug
dotnet run --project alpha-island.csproj -c Debug --no-build
```

Run the language checks with:

```powershell
tools\Test-Maintenance.ps1 -Configuration Debug -Only '--localization-only'
```

The extended regression suite requires the separate `alpha-island-plugins/` source directory beside this repository. Override the MSBuild property `ExternalPluginSourceRoot` for another location. The application solution and main installer build independently of those sources.

## Configuration and privacy

Configuration and local records live under `%LOCALAPPDATA%\alpha-island\config`. API keys are encrypted with Windows DPAPI for the current user and must be entered again on another PC. Do not commit credentials or personal records.

Local language resources are in `PluginSdk/Localization/Strings.en.json`. See the [localization guide](docs/中英文界面支持.md) for contribution rules and validation.

## Contributing and licenses

Report bugs through [Issues](https://github.com/l-egend/alpha-island/issues), including your Windows version, display scaling, reproduction steps and relevant redacted screenshots or logs.

Third-party assets have separate attribution and licenses in their resource folders. Character illustrations and voices remain the property of their respective rights holders; their use is not covered by assumptions about the project code's license. See [welcome assets](Assets/Greetings/SOURCES.md), [sounds](Assets/Sounds/SOURCES.md) and [Bluetooth icons](Assets/Bluetooth/SOURCES.md).
