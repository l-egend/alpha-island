**[简体中文](README.md) | English**

# TrafficMonitor Network Data

Integrate TrafficMonitor's network speeds and valid daily totals as a network data source in Alpha Island.

- Version: **1.0.2**
- Plugin ID: `alpha.trafficmonitor`
- Minimum host plugin API: **6**

## Requirements

Requires both the Alpha Island plugin and the TrafficMonitor native bridge, with TrafficMonitor running.

## Features

- Feed the existing network card, main panel and auxiliary panels without adding another card.
- Read raw upload/download speeds and daily totals when the bridge supplies valid current-day data.
- Use the network adapter selected in TrafficMonitor for a shared source between the applications.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Install the native bridge in TrafficMonitor’s plugin directory and restart TrafficMonitor. See the [native bridge guide](../../Plugins/TrafficMonitorBridge/README.en.md).
2. Install this plugin’s complete folder in Alpha Island, refresh the plugin list and enable it.
3. Choose TrafficMonitor from Alpha Island’s network sources. Both applications must run as the same Windows user.

## Notes and limits

- The destinations differ: the native bridge belongs to TrafficMonitor; this folder containing plugin.json belongs to Alpha Island.
- Enabling this plugin alone does not install the bridge. Missing, invalid or stale bridge data is not fabricated.
- The extension reuses network readings; resource usage varies with configuration. It does not provide other hardware-monitoring data.

Upstream project: [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/TrafficMonitorPlugin/TrafficMonitorPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#trafficmonitor)
