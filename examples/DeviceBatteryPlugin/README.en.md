**[简体中文](README.md) | English**

# 2.4 GHz Device Battery

Read battery information from supported receivers, standard HID devices and controllers, and display it in Alpha Island’s Battery card.

- Version: **1.1.4**
- Plugin ID: `alpha.device-battery`
- Minimum host plugin API: **6**

## Requirements

A supported query protocol or battery field from the device or driver. Python and a separate vendor manager are not required.

## Features

- Supports selected MCHOSE, ATK/VXE, DARKRIM and Logitech HID++ devices, plus standard HID or Windows controller battery reports.
- Combine readings with Windows Bluetooth and earbud sources while retaining their origin and connection state.
- Refresh devices from the Battery card or adjust the query interval in plugin settings.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Connect a supported receiver, wired device or controller, then install and enable the plugin.
2. Open the main Battery card to view readings. This data source does not require a separate plugin card.
3. Refresh the card after a device state change or wait for the automatic update.

## Notes and limits

- An inserted receiver does not prove the peripheral is powered on or connected. Missing readings remain unavailable.
- Voltage-based estimates are labeled, and controller battery levels are not presented as exact percentages.
- K5 Ultra, Logitech and other devices have model and protocol limits. The dedicated Flydigi DInput query has not completed local hardware verification. Queries do not change DPI, lighting, pairing or firmware.

Protocol and interface references: [SOURCES.md](SOURCES.md).

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/DeviceBatteryPlugin/DeviceBatteryPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#device-battery)
