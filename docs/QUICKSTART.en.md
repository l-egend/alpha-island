# Alpha Island quick start

[简体中文](QUICKSTART.zh-CN.md) · [English](QUICKSTART.en.md) · [Project overview](../README.en.md)

## Install and update

Requires x64 Windows 10 version 2004 (19041) or later, or Windows 11. Download and run `AlphaIsland-0.2.32-win-x64-Setup.exe` from the app release. It includes the .NET runtime and normally installs for the current Windows user.

Exit Alpha Island from the tray before updating, then run the installer. Use the existing installation directory to upgrade in place; replacing app files preserves user settings and plugins. Check the user-data option when uninstalling.

## First use

1. Open Settings from the tray and choose your language, appearance and components.
2. Play media to view the top status capsule. Click or approach to expand it; right-click supported content to pin it.
3. Click or scroll the right-hand switch bar to change components, or hold it for the menu. Scrolling within a card only scrolls its contents.
4. Use Screen Tools for OCR, color picking or QR scanning. Speech recognition and region translation are separate modes; region translation first requires a selected screen area.
5. Notifications, location and speech may require Windows permissions. Battery readings depend on hardware, drivers and protocols; unknown values are not treated as 0%.

## Optional plugins

The main installer contains no optional plugins. Download an installable ZIP or the combined bundle from the [plugin release](https://github.com/l-egend/alpha-island/releases/tag/plugins-v0.2.32).

In Settings → Plugins → Open plugin folder, place each complete extracted plugin directory containing `plugin.json` and its DLL, then refresh and enable it. Do not use a source ZIP or copy only the DLL. Exit the app before replacing a complete directory for an update with the same plugin ID.

API Chat requires an endpoint, model and API key. Local Agents requires the relevant tool to be able to hold a conversation already. Finding a program does not verify login or status integration. Follow each plugin's bilingual instructions. TrafficMonitor also requires its native bridge.

## Configuration and data

| Content | Default location |
| --- | --- |
| Settings and local records | `%LOCALAPPDATA%\alpha-island\config` |
| User plugins | `%LOCALAPPDATA%\alpha-island\plugins` |

Use the folder buttons in Settings to confirm the actual locations. Exit the app before backing up these directories. DPAPI-encrypted keys cannot be reused by a different Windows user; enter them again after migration. The app installer contains no developer personal configuration.

## Troubleshooting and feedback

Confirm the version and component switches, then check Windows permissions or the configured service's connection test. Game mode collapses the UI and reduces background work; leave it before checking paused components.

Export a diagnostic bundle from Settings, review the redactions, and attach reproduction steps. Do not submit raw configuration, keys, conversations or private screenshots. For device issues, include the model, connection type and Windows version. For UI issues, include display resolution and scaling.

[App download](https://github.com/l-egend/alpha-island/releases/tag/v0.2.32) · [Plugin catalog](https://github.com/l-egend/alpha-island/tree/plugins) · [Report an issue](https://github.com/l-egend/alpha-island/issues)
