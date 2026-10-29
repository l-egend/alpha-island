**[简体中文](README.md) | English**

# Alpha Island Plugins

Optional plugins for AI conversations, device status, network data and a clock. Install only the plugins you need; the main installer does not include them.

This is the `plugins` branch. The [main application](https://github.com/l-egend/alpha-island/tree/master) is maintained on `master`. The documented host baseline is **0.2.32**, on Windows 10 version 2004 (19041) or later / Windows 11 x64.

<a id="get-plugins"></a>

## Get plugins

Download **installable plugin ZIPs** from the [plugin release](https://github.com/l-egend/alpha-island/releases/tag/plugins-v0.2.32). Choose an individual package or the complete optional bundle. Source archives are for development and cannot be installed directly.

[Download all optional plugins](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/AlphaIsland-optional-plugins-0.2.32.zip) · [Download all plugin sources](https://github.com/l-egend/alpha-island/archive/refs/heads/plugins.zip) · [Browse this branch](https://github.com/l-egend/alpha-island/tree/plugins)

<a id="install"></a>

## Install and update

1. Open Settings → Plugins in Alpha Island, then select **Open plugin folder**. The default user location is `%LOCALAPPDATA%\alpha-island\plugins`; use the folder opened by the app.
2. Extract an **installable plugin ZIP** and place the complete folder containing `plugin.json` there. Keep its DLL, helper files and licenses. Copying one DLL or using the source ZIP directly is not sufficient.
3. Return to Settings, select **Refresh**, and enable the plugin manually. Use **Plugin settings** when configuration is available.
4. Before updating, exit Alpha Island, replace the old folder with the complete new folder for the same ID, then start the app again. Uninstalling may clear plugin-specific settings and is not required for an update.

Expected layout, using API Chat as an example:

```text
plugins/
└── ai.api-chat/
    ├── plugin.json
    ├── AlphaIsland.Plugins.ApiChat.dll
    ├── AlphaIsland.Plugins.ApiChat.deps.json
    ├── README.md
    └── README.en.md
```

TrafficMonitor is the exception that needs **two components in different applications**. Follow its entry below for the native bridge and the Alpha Island plugin.

<a id="catalog"></a>

## Plugin catalog

<a id="api-chat"></a>

### API Chat

Chat with a compatible API model in Alpha Island, with streamed replies, TXT personas, voice commands and optional tool calls.

- Version: **0.1.41**
- Plugin ID: `ai.api-chat`
- Requirements: A working service URL, model name and API key. Voice input also needs a configured speech recognition service.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/ai.api-chat-0.1.41.zip) · [source](examples/ApiChatPlugin) · [instructions](examples/ApiChatPlugin/README.en.md)

---

<a id="local-agents"></a>

### Local Agents

Send tasks to an available local Codex, DeepSeek Harness or Antigravity CLI and view replies and approvals in Alpha Island.

- Version: **0.1.30**
- Plugin ID: `ai.local-agents`
- Requirements: The selected tool must be installed, signed in and usable. Detecting its executable does not verify the account or access permissions.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/ai.local-agents-0.1.30.zip) · [source](examples/LocalAgentsPlugin) · [instructions](examples/LocalAgentsPlugin/README.en.md)

---

<a id="earbuds"></a>

### Earbud Controls

Show per-part battery readings and controls for supported earbuds, with options such as noise control, EQ presets and game mode when available.

- Version: **0.1.6**
- Plugin ID: `oppo.pods`
- Requirements: Usually requires pairing and an audio connection in Windows first. Controls depend on the brand, model, firmware and backend.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/oppo.pods-0.1.6.zip) · [source](examples/OppoPodsPlugin) · [instructions](examples/OppoPodsPlugin/README.en.md)

Reference project: [OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)

---

<a id="vivo-phone"></a>

### vivo Phone Connection

Read existing vivo Office Suite connection records to show the device, connection method, time and local network address, with new-connection notices.

- Version: **0.1.3**
- Plugin ID: `vivo.office`
- Requirements: vivo Office Suite must be installed and have written connection records. Current online status requires the suite to be running with matching device information.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/vivo.office-0.1.3.zip) · [source](examples/VivoOfficePlugin) · [instructions](examples/VivoOfficePlugin/README.en.md)

---

<a id="device-battery"></a>

### 2.4 GHz Device Battery

Read battery information from supported receivers, standard HID devices and controllers, and display it in Alpha Island’s Battery card.

- Version: **1.1.4**
- Plugin ID: `alpha.device-battery`
- Requirements: A supported query protocol or battery field from the device or driver. Python and a separate vendor manager are not required.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/alpha.device-battery-1.1.4.zip) · [source](examples/DeviceBatteryPlugin) · [instructions](examples/DeviceBatteryPlugin/README.en.md)

---

<a id="trafficmonitor"></a>

### TrafficMonitor Network Data

Integrate TrafficMonitor's network speeds and valid daily totals as a network data source in Alpha Island.

- Version: **1.0.2**
- Plugin ID: `alpha.trafficmonitor`
- Requirements: Requires both the Alpha Island plugin and the TrafficMonitor native bridge, with TrafficMonitor running.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/alpha.trafficmonitor-1.0.2.zip) · [source](examples/TrafficMonitorPlugin) · [instructions](examples/TrafficMonitorPlugin/README.en.md)

Upstream project: [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)

---

[Download the optional x64 native bridge](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/TrafficMonitor-integration-0.2.32.zip). This is separate from the Alpha Island plugin; follow the [bridge instructions](Plugins/TrafficMonitorBridge/README.en.md).

<a id="clock"></a>

### Clock Example

Show the time and date with a custom title, seconds, 12/24-hour format and font size; also serves as a plugin development example.

- Version: **1.0.1**
- Plugin ID: `example.clock`
- Requirements: No account, network access or extra application is required. It uses the current Windows local time.
- Get it: [plugin ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/example.clock-1.0.1.zip) · [source](examples/ClockPlugin) · [instructions](examples/ClockPlugin/README.en.md)

---

<a id="development"></a>

## Develop a plugin

Start with the [Clock Example](examples/ClockPlugin/README.en.md#development) and the [SDK contracts](PluginSdk/PluginContracts.cs). The `PluginSdk/` and `ConversationCore/` directories are source snapshots for independent builds. A normal build does not deploy a plugin automatically.

Keep original licenses and attributions when distributing plugins. Build caches, local configuration, command history and workstation release tools are excluded from the source branch.

## Feedback

Use [Issues](https://github.com/l-egend/alpha-island/issues) for bugs, compatibility reports and plugin proposals. Include the Alpha Island version, plugin version, Windows version, device model when relevant, and reproduction steps. Remove keys and personal content from logs and screenshots.
